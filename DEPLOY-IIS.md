# IIS Deployment

1. Install `.NET 8 Hosting Bundle` on the Windows Server.
2. Publish the app:

```powershell
dotnet publish -c Release -o .\publish-iis
```

3. Copy the contents of `publish-iis` to the IIS site folder.
4. Create `appsettings.Production.json` on the server and set:
   - `ConnectionStrings:DefaultConnection`
   - `Security:TokenKey`
5. In IIS:
   - Create an Application Pool with `No Managed Code`
   - Create a Site or Application pointing to the published folder
   - Bind the site to the production hostname and SSL certificate
6. Ensure the IIS server can reach the SQL Server and port `1433`.

Notes:
- In `Development`, the app still uses the local HTTPS certificate for local testing.
- In `Production`, the app is ready to run behind IIS and no longer binds to local dev HTTPS only.
