#!/usr/bin/env bash
#
# Regenerate an unmerged migration without letting EF reconstruct the model
# snapshot.
#
# `dotnet ef migrations remove` rebuilds AppDbContextModelSnapshot.cs from the
# now-last migration's Designer.cs, assuming each Designer holds the cumulative
# model as of that migration. That only holds when migrations are authored in
# the same order their ids sort. This repo hand-dates prefixes into the future
# and routinely lands two PRs that branched off one base out of prefix order, so
# the last Designer by id is missing whatever merged "before" it. EF then diffs
# the current model against that incomplete snapshot and re-emits every column
# in the gap, producing a migration that fails on a fresh database with
# "column ... already exists" naming a table the author never touched.
#
# AppDbContextModelSnapshot.cs is the only file that is reliably the full model,
# because it is regenerated and committed on every add. So: restore it from the
# base ref, delete the migration, and add it again.
#
# Usage:
#   scripts/redo-migration.sh <MigrationName> [--base <ref>]
#
#   <MigrationName>  The name suffix of the migration to redo, without the
#                    timestamp prefix (e.g. AddEmailOutbox).
#   --base <ref>     What to restore the snapshot from. Defaults to origin/main.
#                    Pass the commit before your first migration when the branch
#                    carries more than one.
#
# See issue #794 and the migration-discipline section of CLAUDE.md.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MIGRATIONS_DIR="ALDevToolbox/Data/Migrations"
SNAPSHOT="$MIGRATIONS_DIR/AppDbContextModelSnapshot.cs"
PROJECT="ALDevToolbox"

die() { echo "error: $*" >&2; exit 1; }

NAME=""
BASE_REF="origin/main"
while [ $# -gt 0 ]; do
    case "$1" in
        --base) [ $# -ge 2 ] || die "--base needs a ref"; BASE_REF="$2"; shift 2 ;;
        -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        -*) die "unknown option: $1" ;;
        *) [ -z "$NAME" ] || die "pass one migration name, got '$NAME' and '$1'"; NAME="$1"; shift ;;
    esac
done
[ -n "$NAME" ] || die "usage: scripts/redo-migration.sh <MigrationName> [--base <ref>]"

cd "$REPO_ROOT"

command -v dotnet >/dev/null || die "dotnet is not on PATH"
dotnet ef --version >/dev/null 2>&1 \
    || die "dotnet-ef is not installed. Run: dotnet tool install --global dotnet-ef --version 10.*"

git rev-parse --verify --quiet "$BASE_REF" >/dev/null \
    || die "base ref '$BASE_REF' does not exist. Fetch it first, or pass --base."

# Find the migration by its name suffix. Exactly one must match.
mapfile -t MATCHES < <(find "$MIGRATIONS_DIR" -maxdepth 1 -name "*_${NAME}.cs" -printf '%f\n' | sort)
[ "${#MATCHES[@]}" -gt 0 ] || die "no migration named '$NAME' in $MIGRATIONS_DIR"
[ "${#MATCHES[@]}" -eq 1 ] || die "several migrations match '$NAME': ${MATCHES[*]}"
MIGRATION_FILE="${MATCHES[0]}"
MIGRATION_ID="${MIGRATION_FILE%.cs}"

# Refuse to touch a migration that is already merged: rewriting one changes an
# id that live databases have recorded in __EFMigrationsHistory.
if git cat-file -e "$BASE_REF:$MIGRATIONS_DIR/$MIGRATION_FILE" 2>/dev/null; then
    die "$MIGRATION_ID is already on $BASE_REF. A merged migration is never edited or renumbered (CLAUDE.md). Write a new migration instead."
fi

if [ -n "$(git status --porcelain -- "$MIGRATIONS_DIR" | grep -v "$MIGRATION_ID" || true)" ]; then
    echo "warning: $MIGRATIONS_DIR has other uncommitted changes; they are left alone." >&2
fi

echo "Redoing $MIGRATION_ID against $BASE_REF"

echo "  1/5 restoring the snapshot from $BASE_REF"
git checkout "$BASE_REF" -- "$SNAPSHOT"

echo "  2/5 deleting the old migration"
rm -f "$MIGRATIONS_DIR/$MIGRATION_ID.cs" "$MIGRATIONS_DIR/$MIGRATION_ID.Designer.cs"

echo "  3/5 generating it again"
dotnet ef migrations add "$NAME" --project "$PROJECT" --startup-project "$PROJECT" --output-dir Data/Migrations >/dev/null

mapfile -t REGENERATED < <(find "$MIGRATIONS_DIR" -maxdepth 1 -name "*_${NAME}.cs" -printf '%f\n')
[ "${#REGENERATED[@]}" -eq 1 ] || die "expected one regenerated migration, found ${#REGENERATED[@]}"
NEW_ID="${REGENERATED[0]%.cs}"

# Re-stamp above every existing prefix. CLAUDE.md: pick one strictly greater
# than the current highest, and step clear of any duplicate group rather than
# joining it, so the next author has an unambiguous prefix to step from.
HIGHEST="$(find "$MIGRATIONS_DIR" -maxdepth 1 -name '[0-9]*_*.cs' ! -name '*.Designer.cs' -printf '%f\n' \
    | grep -v "^${NEW_ID}\." | cut -c1-14 | sort | tail -1)"
STAMP="$(date -u -d "${HIGHEST:0:8} + 1 day" +%Y%m%d)000000"
[ "$STAMP" \> "$HIGHEST" ] || die "computed prefix $STAMP is not above the highest existing $HIGHEST"

echo "  4/5 re-stamping $NEW_ID as ${STAMP}_${NAME} (highest existing was $HIGHEST)"
mv "$MIGRATIONS_DIR/$NEW_ID.cs" "$MIGRATIONS_DIR/${STAMP}_${NAME}.cs"
mv "$MIGRATIONS_DIR/$NEW_ID.Designer.cs" "$MIGRATIONS_DIR/${STAMP}_${NAME}.Designer.cs"
# EF orders by the [Migration] id, not the filename, so this is the line that matters.
sed -i "s/Migration(\"$NEW_ID\")/Migration(\"${STAMP}_${NAME}\")/" "$MIGRATIONS_DIR/${STAMP}_${NAME}.Designer.cs"
grep -q "Migration(\"${STAMP}_${NAME}\")" "$MIGRATIONS_DIR/${STAMP}_${NAME}.Designer.cs" \
    || die "failed to rewrite the [Migration] id in ${STAMP}_${NAME}.Designer.cs"

echo "  5/5 checking the model matches the snapshot"
dotnet ef migrations has-pending-model-changes --project "$PROJECT" --startup-project "$PROJECT"

cat <<EOF

Done: $MIGRATIONS_DIR/${STAMP}_${NAME}.cs

Read the Up() body before committing. It must contain only this migration's own
changes; an AddColumn for a table you did not touch means the snapshot you
restored from was not the full model, so re-run with --base pointing further
back.
EOF
