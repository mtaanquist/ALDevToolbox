/* ============================================================
   Data — Object Explorer releases + Cookbook recipes
   ============================================================ */

/* ---- Imported reference packages (Object Explorer landing) ---- */
const REL_MICROSOFT = [
  { label: "Business Central 28.2",  ver: "28.2.50931.51034", files: 15268, imported: "2026-06-12", latest: true },
  { label: "Business Central 28.1",  ver: "28.1.49838.49886", files: 15066, imported: "2026-05-29" },
  { label: "Business Central 28.0",  ver: "28.0.46665.49032", files: 15041, imported: "2026-05-29" },
  { label: "Business Central 27.5",  ver: "27.5.46862.0",     files: 14314, imported: "2026-05-29" },
  { label: "Business Central 27.4",  ver: "27.4.45366.45458", files: 14297, imported: "2026-05-29" },
  { label: "Business Central 27.3",  ver: "27.3.44313.44331", files: 14276, imported: "2026-05-29" },
  { label: "Business Central 27.2",  ver: "27.2.42879.0",     files: 14233, imported: "2026-05-29" },
  { label: "Business Central 27.1",  ver: "27.1.41698.41776", files: 14185, imported: "2026-05-29" },
  { label: "Business Central 27.0",  ver: "27.0.38460.40242", files: 14113, imported: "2026-05-29" },
  { label: "Business Central 18.3",  ver: "18.3.27240.27480", files: 8555,  imported: "2026-06-05" },
  { label: "Business Central 14.52", ver: "14.53.50097.0",    files: 7417,  imported: "2026-05-29" },
];

const REL_THIRDPARTY = [
  { label: "Continia Document Capture", ver: "2026.0.0.0", files: 1842, imported: "2026-06-08", latest: true, pub: "Continia Software" },
  { label: "ForNAV Reports",            ver: "8.4.0.0",    files: 612,  imported: "2026-06-01", pub: "ForNAV" },
  { label: "Tasklet Mobile WMS",        ver: "28.1.0.0",   files: 980,  imported: "2026-05-22", pub: "Tasklet Factory" },
  { label: "Sana Commerce Cloud",       ver: "27.3.0.0",   files: 1455, imported: "2026-05-22", pub: "Sana Commerce" },
];

const REL_SOURCES = {
  microsoft:  REL_MICROSOFT,
  thirdparty: REL_THIRDPARTY,
  customer:   [],
};

/* ---- Cookbook recipes ---- */
const RECIPES = [
  {
    id: "extra-fields",
    title: 'Add extra fields to "Posted Sales Inv. - Update"',
    type: "pattern",
    min: "Business Central 2024 release wave 2",
    minVer: "25.0.0.0",
    desc: 'Shows the pattern for adding additional fields to e.g. the "Posted Sales Inv. - Update" page, so that customers can update things like the External Document No., or the Sell-to Contact, which are often hard requirements for OIOUBL documents.',
    tags: ["oioubl", "sales", "invoice", "header", "update", "document"],
    files: 2,
  },
  {
    id: "doc-attachment",
    title: "Doc. Attachment List Factbox",
    type: "snippet",
    min: "Business Central 2024 release wave 2",
    minVer: "25.0.0.0",
    desc: "Adds support for using the Doc. Attachment List Factbox on record types that Microsoft does not support out of the box. The example shows how to add it for Transfer Headers and Service Items.",
    tags: ["factbox", "documents", "attachment", "attachments"],
    files: 3,
  },
  {
    id: "doc-folders",
    title: "Document Folders factbox (External File Storage)",
    type: "snippet",
    min: "Business Central 2026 release wave 1",
    minVer: "28.0.0.0",
    desc: "Adds a 'Document Files' factbox to any master record card, listing files from a folder in the Ext. File Storage scenario configured for the record's table. File listing happens in a page background task so the host card never blocks on the storage backend. The scope (table + optional filter + folder-path template) is configured per-row in a Document Folder Setup page, so adding a new entity needs no module-code changes — just a tableextension that adds a 'Document Folder Path' field plus a small pageextension that drops the factbox onto the host card.",
    tags: ["document", "folder", "sharepoint", "external", "file", "storage", "factbox", "files", "attachments", "page", "background", "task", "pbt", "drilldown"],
    files: 6,
  },
  {
    id: "http-client",
    title: "HTTP Client Module",
    type: "module",
    min: "Business Central 2024 release wave 2",
    minVer: "25.0.0.0",
    desc: "Drop-in HTTP client module wrapping BC's HttpClient with configurable authorization (No Auth / API Key / Bearer / Basic / OAuth2 Client Credentials and Password grants), per-request configuration via a temporary record, full request/response logging with header capture, automatic 429 rate-limit handling with Retry-After + exponential backoff, and a session-cookie extractor. Includes a SampleApiClient codeunit that demonstrates GET/POST/PUT/PATCH/DELETE against a fictional REST API.",
    tags: ["http", "httpclient", "rest", "api", "oauth2", "bearer", "basic", "auth", "key", "json", "rate", "limit", "retry", "logging", "request", "log"],
    files: 9,
  },
  {
    id: "noseries",
    title: "Number series on master-type records",
    type: "snippet",
    min: "Business Central 2024 release wave 2",
    minVer: "25.0.0.0",
    desc: "Shows the necessary fields and code on a table and page to implement the new Business Foundation version of number series.",
    tags: ["noseries", "number", "series", "numbers", "master"],
    files: 2,
  },
  {
    id: "email-attach",
    title: "Attach report PDF to outgoing email",
    type: "pattern",
    min: "Business Central 2023 release wave 1",
    minVer: "22.0.0.0",
    desc: "Pattern for hooking into the email system so that a report (e.g. a posted sales invoice) is rendered to PDF and attached automatically when the document is sent through the Email feature, without the user having to attach it by hand.",
    tags: ["email", "report", "pdf", "attachment", "sales", "invoice"],
    files: 2,
  },
];

const RECIPE_TYPE_META = {
  snippet: { label: "Snippet", icon: "scissors" },
  pattern: { label: "Pattern", icon: "puzzle" },
  module:  { label: "Module",  icon: "package" },
};

/* ---- Files for the opened recipe (Add extra fields …) ---- */
const RECIPE_FILES = [
  {
    name: "SalesEventHandler.Codeunit.al",
    code: `codeunit 90000 SalesEventHandler
{
    // Any fields that you want to add to the page needs to also be recorded here, or it won't count as being changed
    [EventSubscriber(ObjectType::Page, Page::"Posted Sales Inv. - Update", OnAfterRecordChanged, '', true, true)]
    local procedure CheckUpdatedFields_SalesInvHeader_OnAfterRecordChanged(var SalesInvoiceHeader: Record "Sales Invoice Header"; xSalesInvoiceHeader: Record "Sales Invoice Header"; var IsChanged: Boolean)
    begin
        IsChanged := (SalesInvoiceHeader."External Document No." <> xSalesInvoiceHeader."External Document No.") or
                     (SalesInvoiceHeader."Sell-to Contact" <> xSalesInvoiceHeader."Sell-to Contact") or
                     (SalesInvoiceHeader."Bill-to Contact" <> xSalesInvoiceHeader."Bill-to Contact");
    end;

    // This is the part that sets the values, and should preferably have the same fields in it, as above.
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales Inv. Header - Edit", OnOnRunOnBeforeTestFieldNo, '', false, false)]
    local procedure SetFields_SalesInvHeader_OnOnRunOnBeforeTestFieldNo(var SalesInvoiceHeader: Record "Sales Invoice Header"; SalesInvoiceHeaderRec: Record "Sales Invoice Header")
    begin
        SalesInvoiceHeader."External Document No." := SalesInvoiceHeaderRec."External Document No.";
        SalesInvoiceHeader."Sell-to Contact" := SalesInvoiceHeaderRec."Sell-to Contact";
        SalesInvoiceHeader."Bill-to Contact" := SalesInvoiceHeaderRec."Bill-to Contact";
    end;
}`,
  },
  {
    name: "PostedSalesInvUpdate.PageExt.al",
    code: `pageextension 90000 PostedSalesInvUpdate extends "Posted Sales Inv. - Update"
{
    layout
    {
        addlast("Invoice Details")
        {
            field("CONIT External Document No."; Rec."External Document No.")
            {
                ApplicationArea = All;
                ToolTip = 'Specifies the external document number that is entered on the sales header that this line was posted from.';
            }
            field("CONIT Sell-to Contact"; Rec."Sell-to Contact")
            {
                ApplicationArea = All;
                ToolTip = 'Specifies the name of the contact person at the customer the invoice was sent to.';
                Editable = false;
            }
            field("CONIT Bill-to Contact"; Rec."Bill-to Contact")
            {
                ApplicationArea = All;
                ToolTip = 'Specifies the name of the person you regularly contact when you communicate with the customer to whom the invoice was sent.';
            }
        }
    }
}`,
  },
];

window.REL_SOURCES = REL_SOURCES;
window.RECIPES = RECIPES;
window.RECIPE_TYPE_META = RECIPE_TYPE_META;
window.RECIPE_FILES = RECIPE_FILES;
