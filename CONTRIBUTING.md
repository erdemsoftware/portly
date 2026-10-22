# Contributing to Portly

Thank you! Portly is free and open source; every fix, translation and idea helps.

## Translations (no programming needed)

1. Copy `src/Portly/lang/en.json` to `src/Portly/lang/<code>.json` (for example `nl.json`, `pt-PT.json`).
2. Change `_meta.name` (the language's own name) and `_meta.english`, add yourself to `_meta.authors`.
3. Translate the values, **not** the keys.
4. Keep placeholders such as `{0}` and `{1}` exactly. Keep `\n` (new line) and `\"`.
5. Do not translate product names (Apache, PHP, MySQL, Portly ...).
6. Open a pull request. Missing keys automatically fall back to English, so partial translations are fine.

Check your file:

```powershell
python -c "import json,re,sys;a=json.load(open('src/Portly/lang/en.json',encoding='utf-8'));b=json.load(open(sys.argv[1],encoding='utf-8'));print('missing',set(a)-set(b));print('bad placeholders',[k for k in a if k in b and isinstance(a[k],str) and set(re.findall(r'\{\d\}',a[k]))!=set(re.findall(r'\{\d\}',b[k]))])" src/Portly/lang/nl.json
```

## Package catalog

`manifest/packages.json` is what Windows 10/11 users see in **Packages**. To add or update a package, edit the entry (url, folder, version, optional sha256) and open a pull request. `manifest/packages-legacy.json` is frozen and only changes for broken links.

## Code

- C# for .NET Framework 4.8 (WinForms), no external NuGet packages. Keep it that way: one small exe.
- Build: `dotnet build src\Portly\Portly.csproj -c Release` (add `-p:Edition=Legacy` for the Windows 7 edition).
- Run the end-to-end test before a pull request that touches services or configuration:
  `dotnet run --project tests\Portly.SmokeTest -c Release` (uses ports 8781/8744/3377/... so it runs next to Laragon or XAMPP).
- Security rules for generated PHP configuration: `session.upload_progress.enabled = Off`, `allow_url_include = Off`, never put user input into an `include` path in the dashboard.
