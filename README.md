# QuerySpace C# Edition

QuerySpace C# Edition is a native Windows rebuild of the Python/Qt QuerySpace prototype.

The purpose of this version is to keep the same user workflow while making packaging, code-signing, antivirus review, and company desktop deployment more conservative.

## What is included in this first C# repo

- Native WPF desktop app.
- Workspace folder selection.
- Local dataset explorer.
- SQL scripts explorer.
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
- Export query result to CSV.
- Data quality report with score, null percentages, unique counts, and CPU-only numeric outlier detection.
- ODBC DSN connection.
- Direct SQL Server connection through Microsoft SqlClient.
- Snowflake token connection through Snowflake .NET connector.
- SAP HANA should be used through ODBC DSN in this C# edition unless your company separately approves SAP's ADO.NET/HANA client redistribution.
- Passwords/tokens stored with Windows DPAPI for the current Windows user.
- Base query extraction streams directly into Zstandard-compressed Parquet row groups with progress feedback and cancellation.
- All-or-nothing extraction behavior: partial files are deleted on failure/cancel.
- Saved extract query `.sql` files shown under their connection.
- Dataset information dialog with path, file size, rows, and schema.
- Inno Setup installer script.

## Repository layout

```text
src\QuerySpace\        WPF application source
src\QuerySpace\Assets  QuerySpace icon
installer\             Inno Setup installer script
```

## Third-party libraries

| Library / Technology | Used for | License / note |
|---|---|---|
| .NET 9 / WPF | Native Windows desktop application | MIT, .NET Foundation / Microsoft |
| DuckDB.NET.Data.Full | SQL over Parquet, CSV-to-Parquet conversion, result execution, and bundled native DuckDB runtime | MIT |
| DuckDB native engine | Embedded analytical SQL engine | MIT |
| CsvHelper | CSV handling support | MS-PL / Apache-2.0 dual license, depending package version |
| ExcelDataReader | Read `.xls` and `.xlsx` files | MIT |
| ExcelDataReader.DataSet | DataSet helpers for ExcelDataReader | MIT |
| Microsoft.Data.SqlClient | Direct SQL Server connectivity | MIT |
| System.Data.Odbc | ODBC DSN connectivity | MIT / .NET runtime license |
| Snowflake.Data | Snowflake database connectivity | Apache-2.0 |
| Microsoft.ML | Future/advanced local ML quality checks; current implementation uses CPU-only statistical outlier detection | MIT |
| Parquet.Net | Direct database extraction into Parquet row groups | MIT |
| System.Security.Cryptography.ProtectedData | Windows DPAPI secret encryption | MIT / .NET runtime license |
| Inno Setup 6 | Windows setup executable | Inno Setup license, source available |

Before distributing outside your company, generate a full third-party notice/SBOM from the resolved NuGet dependency tree because transitive dependencies are not fully listed above.

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

```powershell
cd "C:\Users\kiran\source\queryspace-csharp"
dotnet publish .\src\QuerySpace\QuerySpace.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -o .\publish\QuerySpace
```

The app EXE will be:

```text
C:\Users\kiran\source\queryspace-csharp\publish\QuerySpace\QuerySpace.exe
```

## Build installer

Install Inno Setup 6 on the build machine, then run:

```powershell
cd "C:\Users\kiran\source\queryspace-csharp"
dotnet publish .\src\QuerySpace\QuerySpace.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\publish\QuerySpace
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" .\installer\QuerySpace.iss
```

Installer output:

```text
C:\Users\kiran\source\queryspace-csharp\installer-output\QuerySpace-CSharp-Setup.exe
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
7. Right-click the dataset and open **Information**.
8. Save a SQL script and confirm it appears in **SQL Scripts**.
9. Add a SQL Server connection for local Express:

   ```text
   Server: NN007\SQLEXPRESS
   Database: StudentMIS
   Windows integrated authentication: checked
   Require SSL/TLS encryption: checked
   Trust server certificate: checked, if using local dev certs
   ```

10. Run **Extract Base Query** with a small query first:

    ```sql
    select top 100 * from dbo.YourTable;
    ```

11. Confirm extraction creates `workspace\datasets\<dataset_name>.parquet`.
12. Try cancelling a longer extract and confirm partial files are removed.

## Important notes

- This repo intentionally does not use Python, PyInstaller, Qt/PySide, Tauri, Node.js, or FastAPI.
- There is no local HTTP backend, so there is no localhost API for other local processes to call.
- SAP HANA direct ADO.NET support is not bundled. Use SAP HANA through ODBC DSN unless SAP licensing/deployment is separately approved.
- For very large extracts, QuerySpace writes database rows directly into Parquet row groups under a `.partial` file and renames it only after the extract succeeds. Cancelled or failed extracts delete the partial file.
