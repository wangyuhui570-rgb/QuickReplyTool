namespace QuickReply.Desktop.Services;

internal interface IPatchInstallationStateService
{
    Task MarkPatchVersionInstalledAsync(string patchVersion);
}
