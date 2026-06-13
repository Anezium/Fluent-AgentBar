$ErrorActionPreference = "Stop"

dotnet restore winui\FluentAgentBar.csproj -p:NuGetAudit=true
dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64
dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug
