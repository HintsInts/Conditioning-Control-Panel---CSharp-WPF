CCP × CHASTER — GITHUB DROP-IN
==============================

Purpose
-------
This package adds the tiny CCP reporting client used by the Chaster/Railway Discipline Engine.
CCP only pairs once and reports session_started/session_ended. All streak/shop/punishment logic remains on Chaster/Railway.

Do this
-------
1. In GitHub Desktop, open your fork of Conditioning-Control-Panel---CSharp-WPF.
2. Create/switch to branch: feature/chaster-integration
3. In GitHub Desktop choose Repository -> Show in Explorer.
4. Extract EVERYTHING from this ZIP directly into that repository root.
   You should see this README beside the repository's ConditioningControlPanel folder.
5. Double-click: _INSTALL_CHASTER_ADDON.bat
6. It should say SUCCESS.
7. Optional: double-click _REMOVE_INSTALLER_FILES_AFTER_SUCCESS.bat so installer helper files are not committed.
8. In GitHub Desktop review the changed files.
9. Commit with e.g. "Add Chaster CCP integration".
10. Click Push origin.
11. Build/test CCP on Windows before merging the branch into main.

Expected source changes
-----------------------
NEW:
  ConditioningControlPanel/Services/Integrations/Chaster/ChasterBootstrap.cs
  ConditioningControlPanel/Services/Integrations/Chaster/ChasterCcpClient.cs
  ConditioningControlPanel/Services/Integrations/Chaster/ChasterCredentialStore.cs
  ConditioningControlPanel/Services/Integrations/Chaster/ChasterIntegrationLog.cs
  ConditioningControlPanel/Services/Integrations/Chaster/ChasterModels.cs
  ConditioningControlPanel/Services/Integrations/Chaster/ChasterOutbox.cs
  ConditioningControlPanel/Views/Controls/AppSettings/DevicesSettingsSection.Chaster.cs

MODIFIED BY INSTALLER:
  ConditioningControlPanel/Services/Session/SessionEngine.cs
  ConditioningControlPanel/Views/Controls/AppSettings/DevicesSettingsSection.xaml

No new NuGet packages are required.

After building CCP
------------------
Open:
  Settings -> Devices -> Chaster connection

Enter:
  Extension server = your Railway base URL, e.g. https://example.up.railway.app
  Connection code  = the one-time code shown by the Chaster extension

Then in Chaster press "Test CCP Connection", start a CCP session and end that same session.
Expected result: TEST PASSED.

Safety
------
The installer is idempotent: running it twice does not duplicate hooks/cards.
Before modifying the two existing files it creates *.pre-chaster-addon.bak backups if no backup exists yet.
Do not run this on main. Use the feature branch first.
