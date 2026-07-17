@echo off
rem Build + launch the app in RELEASE (the fast config) on your real drives.
rem Once it opens, click "Restart as administrator" in the top bar to index them.
rem Keep this console window open while using the app; closing it closes the app.
cd /d "%~dp0"
dotnet run --project "src\DreamOfElectricStorage.App\DreamOfElectricStorage.App.csproj" -c Release --launch-profile "DreamOfElectricStorage.App (Unpackaged)"
