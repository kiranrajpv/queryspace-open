# QuerySpace

QuerySpace is a native Windows desktop application for working with local Parquet datasets and database extracts.

It provides a local SQL workspace for importing CSV, Excel, and Parquet files, extracting data from supported databases, storing extracts as Parquet, querying datasets with DuckDB SQL, exporting results, and running data-quality and SAP material transformation analysis.

## Features

- Native WPF desktop app.
- Workspace folder selection.
- Local dataset explorer.
- SQL scripts explorer.
- Built-in SQL Templates explorer for Extract, Transform/Pre-Load, and Post-Load validation starting points.
- Connections explorer.
- Import CSV, Excel, or Parquet into workspace-managed Parquet datasets.
- Normalize common Excel/CSV null markers such as `#N/A`, `#VALUE!`, `#REF!`, `#DIV/0!`, `NULL`, `N/A`, and blanks.
- Deterministic dataset files: `workspace\datasets\<dataset_name>.parquet`.
- Duplicate dataset names blocked. User must delete the existing dataset or choose a different name.
- DuckDB SQL over local Parquet datasets.
- Memory mode by default.
- Read-only mode for SELECT-style queries.
- Multiple-statement SQL script execution in memory mode.
- Preview row limit textbox.
- Export query result to CSV, Excel `.xlsx`, or Parquet. CSV and Parquet exports stream through DuckDB; Excel exports stream to `.xlsx` and is limited by Excel's 1,048,576-row sheet limit. Exports write to a `.partial` file first and only replace the final file after success.
- Data quality report with score, null percentages, unique counts, and CPU-only numeric outlier detection.
- Tagged SQL transformation analysis for SAP Material projects: validates ECC extract/source columns, pre-load transformed columns, cross-reference usage, and S/4 MDG post-load values from the query result grid.
- ODBC DSN connection with an editable dropdown populated from Windows ODBC Administrator DSNs.
- Direct SQL Server connection through Microsoft SqlClient.
- Snowflake token connection through Snowflake .NET connector.
- SAP HANA should be used through ODBC DSN unless your company separately approves SAP's ADO.NET/HANA client redistribution.
- Passwords/tokens stored with Windows DPAPI for the current Windows user.
- Base query extraction streams directly into Zstandard-compressed Parquet row groups with progress feedback and cancellation.
- Extraction defaults to 250,000-row batches/row groups for better large-extract throughput and compression.
- All-or-nothing extraction behavior: partial files are deleted on failure/cancel.
- Saved extract query `.sql` files shown under their connection; opening one reopens the Extract Base Query dialog pre-filled so it can be rerun.
- Dataset information dialog with path, file size, rows, and schema.
- Inno Setup installer script.

## Repository layout

```text
src\QuerySpace\        WPF application source
src\QuerySpace\Assets  QuerySpace icon
src\QuerySpace\Templates Built-in SQL templates copied into the published app
installer\             Inno Setup installer script
```

## Technology, tools, libraries, and license inventory

This section documents the technologies used by QuerySpace, why they are used, and the license information available from project files/NuGet package metadata on this build machine.

Important: this table is an engineering inventory, not legal advice. Before broad internal rollout or any external/public release, generate a formal third-party notice/SBOM from the exact release build output and have company legal/compliance review it.

Package-provided license and notice text discovered in the local NuGet package cache is included in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Regenerate it with [scripts/generate-third-party-notices.ps1](scripts/generate-third-party-notices.ps1) whenever package versions change.

### Project/runtime/build technologies

| Technology / tool | Version / source | License / owner | Why QuerySpace uses it |
|---|---:|---|---|
| C# | .NET SDK toolchain | Microsoft / .NET Foundation | Main implementation language for the Windows desktop application. |
| .NET | 9.0 target, `net9.0-windows` | MIT, Microsoft / .NET Foundation | Application runtime, base class libraries, cryptography APIs, WPF support, packaging/publish toolchain. |
| WPF | Included with .NET desktop workload | MIT, Microsoft / .NET Foundation | Native Windows UI: windows, dialogs, tree views, editor text boxes, result grid, status bar, progress bar. |
| Windows DWM API | Windows OS API | Microsoft Windows platform API | Colors the native Windows title bar Parker blue with white title text. |
| Windows DPAPI | Windows OS API via `ProtectedData` | Microsoft Windows platform API | Encrypts stored connection passwords/tokens for the current Windows user. |
| Windows ODBC Administrator | Windows OS tool | Microsoft Windows platform component | Users create/manage 64-bit ODBC DSNs for SQL Server, SAP HANA, and other ODBC sources. |
| Git | Developer tool | GPL-2.0 | Source control for the repository. |
| NuGet | .NET package manager | Apache-2.0 / Microsoft ecosystem tooling | Restores third-party .NET packages. |
| Inno Setup 6 | Installer tool | Inno Setup License; source available | Builds the Windows installer executable and desktop/start-menu shortcuts. |
| QuerySpace icon asset | `src\QuerySpace\Assets\QuerySpace.ico` | Project asset | Application/window/installer icon. Keep provenance/license with project assets if replaced. |

### Direct NuGet dependencies

These are referenced directly in [QuerySpace.csproj](src/QuerySpace/QuerySpace.csproj).

| Package | Version | License from NuGet metadata | Why QuerySpace uses it |
|---|---:|---|---|
| `CommunityToolkit.Mvvm` | 8.4.2 | MIT | MVVM utility package available for UI binding/view-model work. |
| `CsvHelper` | 33.1.0 | MS-PL OR Apache-2.0 | CSV handling support for import/export workflows. |
| `DuckDB.NET.Data.Full` | 1.5.3 | MIT | Embedded DuckDB ADO.NET provider plus bundled native DuckDB runtime. Used for local SQL over Parquet, CSV-to-Parquet import, result previews, and CSV export. |
| `ExcelDataReader` | 3.9.0 | MIT | Reads `.xls` and `.xlsx` files before converting them to workspace Parquet datasets. |
| `ExcelDataReader.DataSet` | 3.9.0 | MIT | ExcelDataReader helper package for dataset-style Excel handling. |
| `Microsoft.Data.SqlClient` | 7.0.2 | MIT | Direct Microsoft SQL Server connection support without requiring ODBC DSN. |
| `Microsoft.ML` | 5.0.0 | MIT | Supports local CPU-only data-quality analysis capabilities. |
| `Parquet.Net` | 6.0.3 | MIT | Writes database extraction results directly into Parquet row groups with Zstandard compression. |
| `Snowflake.Data` | 5.7.0 | Apache-2.0 | Direct Snowflake connection/extraction support using user token/auth configuration. |
| `System.Data.Odbc` | 9.0.7 | .NET runtime package; license metadata not declared in local `.nuspec` | ODBC DSN connectivity for SQL Server, SAP HANA, and other user-configured ODBC data sources. |
| `System.Security.Cryptography.ProtectedData` | 9.0.13 | MIT | Provides Windows DPAPI integration for encrypted secret storage. |

### Transitive NuGet dependencies

These are pulled by the direct dependencies above. Versions are from `dotnet list package --include-transitive`.

| Package | Version | License from NuGet metadata | Why it is present |
|---|---:|---|---|
| `Apache.Arrow` | 14.0.2 | `LICENSE.txt` in package; Apache Arrow project license is Apache-2.0 | Transitive dependency used by data/connector packages for columnar data handling. |
| `AWSSDK.Core` | 4.0.3.28 | Apache-2.0 | Transitive dependency of Snowflake/cloud storage support. |
| `AWSSDK.S3` | 4.0.20.4 | Apache-2.0 | Transitive dependency of Snowflake/cloud storage support. |
| `Azure.Core` | 1.25.0 | MIT | Transitive Azure SDK base dependency. |
| `Azure.Storage.Blobs` | 12.13.0 | MIT | Transitive dependency for connector/cloud storage scenarios. |
| `Azure.Storage.Common` | 12.12.0 | MIT | Transitive Azure Storage support. |
| `BouncyCastle.Cryptography` | 2.6.2 | License metadata not declared in local `.nuspec`; verify package license before redistribution | Transitive cryptography dependency used by connector/security packages. |
| `CommunityToolkit.HighPerformance` | 8.4.2 | MIT | Transitive dependency of CommunityToolkit packages. |
| `DuckDB.NET.Bindings.Full` | 1.5.3 | License metadata not declared in local `.nuspec`; DuckDB.NET project is MIT | Native DuckDB bindings bundled by `DuckDB.NET.Data.Full`. |
| `Google.Api.Gax` | 4.12.1 | BSD-3-Clause | Transitive Google API support used by cloud connector packages. |
| `Google.Api.Gax.Rest` | 4.12.1 | BSD-3-Clause | Transitive REST support for Google API packages. |
| `Google.Apis` | 1.72.0 | Apache-2.0 | Transitive Google API client dependency. |
| `Google.Apis.Auth` | 1.72.0 | Apache-2.0 | Transitive Google auth dependency. |
| `Google.Apis.Core` | 1.72.0 | Apache-2.0 | Transitive Google API core dependency. |
| `Google.Apis.Storage.v1` | 1.71.0.3920 | Apache-2.0 | Transitive Google Cloud Storage API dependency. |
| `Google.Cloud.Storage.V1` | 4.14.0 | Apache-2.0 | Transitive Google Cloud Storage dependency. |
| `K4os.Compression.LZ4` | 1.3.8 | License URL in package metadata; verify before redistribution | LZ4 compression support used transitively by data/Parquet-related packages. |
| `Microsoft.Bcl.AsyncInterfaces` | 9.0.4 | MIT | .NET async interface support for packages targeting multiple frameworks. |
| `Microsoft.Bcl.Cryptography` | 9.0.13 | MIT | Cryptography support library used transitively by Microsoft packages. |
| `Microsoft.Data.SqlClient.Extensions.Abstractions` | 7.0.2 | MIT | Transitive SQL Client extension abstractions. |
| `Microsoft.Data.SqlClient.Internal.Logging` | 7.0.2 | License metadata not declared in local `.nuspec`; verify before redistribution | Transitive SQL Client logging package. |
| `Microsoft.Data.SqlClient.SNI.runtime` | 6.0.2 | License metadata not declared in local `.nuspec`; verify before redistribution | Native SQL Server network interface runtime used by SqlClient. |
| `Microsoft.Extensions.Caching.Abstractions` | 9.0.13 | MIT | Transitive Microsoft.Extensions support. |
| `Microsoft.Extensions.Caching.Memory` | 9.0.13 | MIT | Transitive in-memory cache support. |
| `Microsoft.Extensions.DependencyInjection` | 9.0.5 | MIT | Transitive dependency injection support. |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 9.0.13 | MIT | Transitive dependency injection abstractions. |
| `Microsoft.Extensions.Logging` | 9.0.5 | MIT | Transitive logging support. |
| `Microsoft.Extensions.Logging.Abstractions` | 9.0.13 | MIT | Transitive logging abstractions. |
| `Microsoft.Extensions.Options` | 9.0.13 | MIT | Transitive options/configuration support. |
| `Microsoft.Extensions.Primitives` | 9.0.13 | MIT | Transitive Microsoft.Extensions primitives. |
| `Microsoft.IdentityModel.Abstractions` | 8.16.0 | License metadata not declared in local `.nuspec`; verify before redistribution | Transitive identity/token abstraction support. |
| `Microsoft.IdentityModel.JsonWebTokens` | 8.16.0 | MIT | Transitive JWT support. |
| `Microsoft.IdentityModel.Logging` | 8.16.0 | MIT | Transitive IdentityModel logging support. |
| `Microsoft.IdentityModel.Protocols` | 8.16.0 | MIT | Transitive identity protocol support. |
| `Microsoft.IdentityModel.Protocols.OpenIdConnect` | 8.16.0 | MIT | Transitive OpenID Connect protocol support. |
| `Microsoft.IdentityModel.Tokens` | 8.16.0 | MIT | Transitive token validation/handling support. |
| `Microsoft.IO.RecyclableMemoryStream` | 3.0.1 | MIT | Transitive memory-stream optimization dependency. |
| `Microsoft.ML.CpuMath` | 5.0.0 | MIT | CPU math runtime for ML.NET. |
| `Microsoft.ML.DataView` | 5.0.0 | MIT | ML.NET data-view abstractions. |
| `Microsoft.SqlServer.Server` | 1.0.0 | License metadata not declared in local `.nuspec`; verify before redistribution | Transitive SQL Server type/support dependency. |
| `Newtonsoft.Json` | 13.0.4 | MIT | Transitive JSON serialization dependency used by connector packages. |
| `Snappier` | 1.3.1 | BSD-3-Clause | Snappy compression support used by Parquet/Arrow-style data packages. |
| `System.Buffers` | 4.6.1 | License metadata not declared in local `.nuspec`; .NET library packages are generally MIT but verify before redistribution | Transitive buffer primitives. |
| `System.CodeDom` | 9.0.4 | License metadata not declared in local `.nuspec`; verify before redistribution | Transitive CodeDOM support. |
| `System.Collections.Immutable` | 9.0.4 | MIT | Immutable collection primitives used transitively. |
| `System.Configuration.ConfigurationManager` | 9.0.13 | MIT | Configuration support required by some database/connector packages. |
| `System.Diagnostics.DiagnosticSource` | 6.0.2 | MIT | Diagnostic/activity tracing support. |
| `System.Diagnostics.EventLog` | 9.0.13 | MIT | Windows Event Log support used transitively by Microsoft packages. |
| `System.IdentityModel.Tokens.Jwt` | 8.16.0 | MIT | JWT support used by identity/auth connector dependencies. |
| `System.IO.Hashing` | 6.0.0 | MIT | Hashing primitives used transitively. |
| `System.Management` | 7.0.2 | MIT | Windows management APIs used transitively by SQL/client packages. |
| `System.Memory` | 4.6.3 | MIT | Memory primitives used by many packages. |
| `System.Memory.Data` | 1.0.2 | MIT | Binary/memory data support used transitively. |
| `System.Numerics.Tensors` | 9.0.4 | MIT | Tensor primitives used by ML.NET. |
| `System.Numerics.Vectors` | 4.5.0 | License URL in package metadata; .NET library package, verify before redistribution | SIMD vector primitives used by math/ML packages. |
| `System.Reflection.Emit.Lightweight` | 4.7.0 | MIT | Lightweight dynamic-code support used transitively. |
| `System.Runtime.CompilerServices.Unsafe` | 6.1.2 | License metadata not declared in local `.nuspec`; verify before redistribution | Low-level runtime helper package used by performance-sensitive dependencies. |
| `System.Security.Cryptography.Pkcs` | 9.0.13 | MIT | PKCS cryptography support used transitively. |
| `System.Text.Encodings.Web` | 4.7.2 | MIT | Web-safe text encoding support used by JSON/auth packages. |
| `System.Text.Json` | 4.7.2 | MIT | JSON serialization support used transitively. |
| `System.Threading.Channels` | 9.0.4 | MIT | Async channel support used by connector/runtime packages. |
| `System.Threading.Tasks.Extensions` | 4.5.4 | License URL in package metadata; .NET library package, verify before redistribution | Async task compatibility helpers. |
| `TimeZoneConverter` | 7.2.0 | MIT | Time zone conversion support used by Snowflake connector. |
| `Tomlyn.Signed` | 0.17.0 | License metadata not declared in local `.nuspec`; verify before redistribution | TOML parsing support pulled transitively. |
| `ZstdSharp.Port` | 0.8.8 | MIT | Zstandard compression support used by Parquet writing. |

### External systems/drivers not bundled by QuerySpace

| External component | Bundled with QuerySpace? | License / owner | Why a user may need it |
|---|---:|---|---|
| Microsoft SQL Server / SQL Server Express | No | Microsoft SQL Server license terms | Optional database source for base-query extraction. QuerySpace connects through `Microsoft.Data.SqlClient`. |
| SQL Server TLS certificate / company root certificate | No | Company/Microsoft certificate infrastructure | Required for trusted encrypted SQL Server connections without `Trust server certificate`. |
| SAP HANA Client 64-bit / HDBODBC driver | No | SAP license terms; obtain through company SAP portal/SAP Software Download Center | Required when users connect to SAP HANA through a 64-bit ODBC DSN. QuerySpace does not redistribute SAP HANA drivers. |
| 64-bit ODBC DSNs | No | Configured by user/admin on Windows | Lets QuerySpace connect to HANA, SQL Server, and other ODBC databases through Windows ODBC Administrator. |
| Snowflake account/token | No | Snowflake terms | Optional Snowflake base-query extraction through `Snowflake.Data`. |

### Dependency/security maintenance commands

Run these before releasing a new installer:

```powershell
cd "C:\Users\kiran\source\queryspace-csharp"
dotnet restore .\QuerySpace.sln
dotnet list .\src\QuerySpace\QuerySpace.csproj package --include-transitive
dotnet list .\src\QuerySpace\QuerySpace.csproj package --vulnerable --include-transitive
dotnet list .\src\QuerySpace\QuerySpace.csproj package --outdated
.\scripts\generate-third-party-notices.ps1
```

At the time this README section was updated, `dotnet list package --vulnerable --include-transitive` reported no known vulnerable packages from the configured NuGet sources.

## Build from source

Open PowerShell:

```powershell
cd "C:\Users\kiran\source\queryspace-csharp"
dotnet restore .\QuerySpace.sln
dotnet build .\QuerySpace.sln -c Release
```

Run from source:

```powershell
dotnet run --project .\src\QuerySpace\QuerySpace.csproj -c Release
```

## Publish a self-contained Windows folder

Use the installer build script with `-SkipInstaller` when you only need the published application folder:

```powershell
cd "C:\Users\kiran\source\queryspace-csharp"
.\scripts\build-installer.ps1 -SkipInstaller
```

The app EXE will be:

```text
C:\Users\kiran\source\queryspace-csharp\publish\QuerySpace\QuerySpace.exe
```

## Build installer

The setup/install project is [installer\QuerySpace.iss](installer/QuerySpace.iss). It is built by [scripts\build-installer.ps1](scripts/build-installer.ps1).

Install Inno Setup 6 on the build machine, then run:

```powershell
cd "C:\Users\kiran\source\queryspace-csharp"
.\scripts\build-installer.ps1
```

Installer output:

```text
C:\Users\kiran\source\queryspace-csharp\installer-output\QuerySpace-Setup-0.1.0.exe
```

The installer includes the app files, desktop/start-menu shortcuts, [LICENSE](LICENSE), [README.md](README.md), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Use `-FrameworkDependent` if your company deployment standard is to install the .NET Desktop Runtime separately instead of creating a self-contained installer.

## SAP Material transformation analysis from tagged SQL

Run a DuckDB query that returns source/ECC columns, transformed/pre-load columns, optional cross-reference columns, and optional MDG post-load columns. Add tags as SQL comments, then click **Analyze Transform**.

QuerySpace includes three built-in templates in the **Templates** tab:

| Template | Use it for |
|---|---|
| `01_extract_base_query` | Starting point for extracting ECC/SAP source data through **Extract Base Query**. |
| `02_transform_preload_analysis` | Compare ECC extract rows to transformed/pre-load rows and validate cross-reference usage. |
| `03_post_load_validation` | Compare ECC source, transformed/pre-load, cross-reference, and S/4 MDG post-load rows. |

Double-click a template to load it into the Query editor, then replace dataset/table/column names with your project names.

Useful tags:

| Tag | Purpose |
|---|---|
| `@analysis` | Analysis name, for example `sap-material-transformation`. |
| `@source` / `@extract` | ECC extract/source dataset name. |
| `@target` / `@preload` | Transformed pre-load dataset name. |
| `@postload` | S/4 MDG post-load dataset name. |
| `@xref` | Cross-reference dataset used by the transformation. |
| `@keys` | Business key columns returned in the result. |
| `@source-prefix` / `@extract-prefix` | Prefix for ECC/source columns in the result, default `source_`. |
| `@target-prefix` / `@preload-prefix` | Prefix for transformed/pre-load columns, default `transformed_`. |
| `@postload-prefix` | Prefix for MDG post-load columns, default `mdg_`. |
| `@xref-prefix` | Prefix for cross-reference columns, default `xref_`. |
| `@compare` | Explicit source/transformed column pairs, for example `source_matnr=transformed_matnr`. |
| `@xref-values` | Transformed column to expected xref column, for example `transformed_matnr=xref_mdg_matnr`. |
| `@xref-valid-from` / `@xref-valid-to` | Cross-reference validity date columns. |
| `@business-date` | Date used for validity checks. |
| `@rules` | Rules to run. If omitted, QuerySpace runs the standard SAP material transformation rule set. |
| `@numeric-tolerance` | Allowed numeric difference for quantity/amount fields. |
| `@date-tolerance-days` | Allowed date difference. |
| `@xref-default-values` | Default/unmapped values to flag, for example `UNKNOWN,N/A,UNMAPPED`. |

Supported rules include `null-introduced`, `value-changed`, `numeric-drift`, `date-drift`, `leading-zero-loss`, `case-change`, `trim-change`, `xref-missing`, `xref-mismatch`, `xref-expired`, `xref-default-used`, `duplicate-key`, `postload-mismatch`, and `distribution-shift`.

Example:

```sql
-- @analysis: sap-material-transformation
-- @source: ecc_material_extract
-- @target: material_preload
-- @postload: mdg_material_postload
-- @xref: material_crossref
-- @keys: source_matnr, source_werks
-- @source-prefix: source_
-- @target-prefix: transformed_
-- @postload-prefix: mdg_
-- @xref-prefix: xref_
-- @compare: source_matnr=transformed_matnr, source_werks=transformed_werks, source_meins=transformed_meins, source_mtart=transformed_mtart
-- @xref-values: transformed_matnr=xref_mdg_matnr, transformed_meins=xref_target_uom
-- @xref-valid-from: xref_valid_from
-- @xref-valid-to: xref_valid_to
-- @business-date: 2026-07-04
-- @numeric-tolerance: 0.01
-- @date-tolerance-days: 1
-- @xref-default-values: UNKNOWN,N/A,UNMAPPED
-- @rules: null-introduced, value-changed, numeric-drift, leading-zero-loss, xref-missing, xref-mismatch, xref-expired, xref-default-used, duplicate-key, postload-mismatch, distribution-shift

select
  s.matnr as source_matnr,
  s.werks as source_werks,
  s.meins as source_meins,
  s.mtart as source_mtart,
  s.lvorm as source_lvorm,
  t.matnr as transformed_matnr,
  t.werks as transformed_werks,
  t.meins as transformed_meins,
  t.mtart as transformed_mtart,
  x.legacy_matnr as xref_legacy_matnr,
  x.mdg_matnr as xref_mdg_matnr,
  x.target_uom as xref_target_uom,
  x.valid_from as xref_valid_from,
  x.valid_to as xref_valid_to,
  m.matnr as mdg_matnr,
  m.werks as mdg_werks,
  m.meins as mdg_meins,
  m.mtart as mdg_mtart
from ecc_material_extract s
left join material_crossref x
  on s.matnr = x.legacy_matnr
 and s.werks = x.legacy_werks
left join material_preload t
  on x.mdg_matnr = t.matnr
 and s.werks = t.werks
left join mdg_material_postload m
  on t.matnr = m.matnr
 and t.werks = m.werks;
```

## Manual test checklist

1. Launch QuerySpace.
2. Click **Set Workspace** and choose a test folder.
3. Import a small CSV.
4. Confirm it appears in **Local Datasets** as only the logical dataset name.
5. Run:

   ```sql
   select * from your_dataset_name limit 100;
   ```

6. Click **Analyze Quality**.
7. Click **Export Result**, choose CSV, Excel, or Parquet, and confirm the file is written under the workspace exports folder or chosen path.
8. Right-click the dataset and open **Information**.
9. Save a SQL script and confirm it appears in **SQL Scripts**.
10. Add a SQL Server connection for local Express:

   ```text
   Server: NN007\SQLEXPRESS
   Database: StudentMIS
   Windows integrated authentication: checked
   Require SSL/TLS encryption: checked
   Trust server certificate: checked, if using local dev certs
   ```

11. Run **Extract Base Query** with a small query first:

    ```sql
    select top 100 * from dbo.YourTable;
    ```

12. Confirm extraction creates `workspace\datasets\<dataset_name>.parquet`.
13. Try cancelling a longer extract and confirm partial files are removed.
14. Run a tagged SAP material transformation query that returns source/transformed/xref/MDG columns.
15. Click **Analyze Transform** and review **Overview**, **Issues**, **Column Pairs**, **Cross Reference**, **Recommendations**, and **Tags Used**.

## Important notes

- There is no local HTTP backend, so there is no localhost API for other local processes to call.
- SAP HANA direct ADO.NET support is not bundled. Use SAP HANA through ODBC DSN unless SAP licensing/deployment is separately approved.
- For very large extracts, QuerySpace writes database rows directly into Parquet row groups under a `.partial` file and renames it only after the extract succeeds. Cancelled or failed extracts delete the partial file.
- SQL scripts saved directly by the user appear in **SQL Scripts**. Base extraction SQL appears under the source connection to avoid showing the same query in two places.
