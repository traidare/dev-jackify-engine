
using System;
using Microsoft.Extensions.DependencyInjection;
using Wabbajack.CLI.Builder;
using Wabbajack.CLI.Verbs;

namespace Wabbajack.CLI;

public class VerbRegistration
{
    public static void RegisterVerbs(CommandLineBuilder CommandLineBuilder, IServiceCollection services)
    {
        CommandLineBuilder.RegisterCommand<Install>(Install.Definition, c => ((Install)c).Run);
        services.AddSingleton<Install>();
        CommandLineBuilder.RegisterCommand<InstallCompileInstallVerify>(InstallCompileInstallVerify.Definition, c => ((InstallCompileInstallVerify)c).Run);
        services.AddSingleton<InstallCompileInstallVerify>();
        CommandLineBuilder.RegisterCommand<ValidateLists>(ValidateLists.Definition, c => ((ValidateLists)c).Run);
        services.AddSingleton<ValidateLists>();
        CommandLineBuilder.RegisterCommand<MirrorFile>(MirrorFile.Definition, c => ((MirrorFile)c).Run);
        services.AddSingleton<MirrorFile>();
        CommandLineBuilder.RegisterCommand<ModlistReport>(ModlistReport.Definition, c => ((ModlistReport)c).Run);
        services.AddSingleton<ModlistReport>();
        CommandLineBuilder.RegisterCommand<Reset>(Reset.Definition, c => ((Reset)c).Run);
        services.AddSingleton<Reset>();
        CommandLineBuilder.RegisterCommand<Encrypt>(Encrypt.Definition, c => ((Encrypt)c).Run);
        services.AddSingleton<Encrypt>();
        CommandLineBuilder.RegisterCommand<Decrypt>(Decrypt.Definition, c => ((Decrypt)c).Run);
        services.AddSingleton<Decrypt>();
        CommandLineBuilder.RegisterCommand<DownloadAll>(DownloadAll.Definition, c => ((DownloadAll)c).Run);
        services.AddSingleton<DownloadAll>();
        CommandLineBuilder.RegisterCommand<UploadToNexus>(UploadToNexus.Definition, c => ((UploadToNexus)c).Run);
        services.AddSingleton<UploadToNexus>();
        CommandLineBuilder.RegisterCommand<VFSIndex>(VFSIndex.Definition, c => ((VFSIndex)c).Run);
        services.AddSingleton<VFSIndex>();
        CommandLineBuilder.RegisterCommand<IndexNexusMod>(IndexNexusMod.Definition, c => ((IndexNexusMod)c).Run);
        services.AddSingleton<IndexNexusMod>();
        CommandLineBuilder.RegisterCommand<MegaLogin>(MegaLogin.Definition, c => ((MegaLogin)c).Run);
        services.AddSingleton<MegaLogin>();
        CommandLineBuilder.RegisterCommand<ListModlists>(ListModlists.Definition, c => ((ListModlists)c).Run);
        services.AddSingleton<ListModlists>();
        CommandLineBuilder.RegisterCommand<ListGames>(ListGames.Definition, c => ((ListGames)c).Run);
        services.AddSingleton<ListGames>();
        CommandLineBuilder.RegisterCommand<HashFile>(HashFile.Definition, c => ((HashFile)c).Run);
        services.AddSingleton<HashFile>();
        CommandLineBuilder.RegisterCommand<HashGameFiles>(HashGameFiles.Definition, c => ((HashGameFiles)c).Run);
        services.AddSingleton<HashGameFiles>();
        CommandLineBuilder.RegisterCommand<HashUrlString>(HashUrlString.Definition, c => ((HashUrlString)c).Run);
        services.AddSingleton<HashUrlString>();
        CommandLineBuilder.RegisterCommand<Compile>(Compile.Definition, c => ((Compile)c).Run);
        services.AddSingleton<Compile>();
        CommandLineBuilder.RegisterCommand<VerifyModlistInstall>(VerifyModlistInstall.Definition, c => ((VerifyModlistInstall)c).Run);
        services.AddSingleton<VerifyModlistInstall>();
        CommandLineBuilder.RegisterCommand<Extract>(Extract.Definition, c => ((Extract)c).Run);
        services.AddSingleton<Extract>();
        CommandLineBuilder.RegisterCommand<DownloadWabbajackFile>(DownloadWabbajackFile.Definition, c => ((DownloadWabbajackFile)c).Run);
        services.AddSingleton<DownloadWabbajackFile>();
        CommandLineBuilder.RegisterCommand<DownloadUrl>(DownloadUrl.Definition, c => ((DownloadUrl)c).Run);
        services.AddSingleton<DownloadUrl>();
        CommandLineBuilder.RegisterCommand<GetModlistUrl>(GetModlistUrl.Definition, c => ((GetModlistUrl)c).Run);
        services.AddSingleton<GetModlistUrl>();
        CommandLineBuilder.RegisterCommand<Changelog>(Changelog.Definition, c => ((Changelog)c).Run);
        services.AddSingleton<Changelog>();
        CommandLineBuilder.RegisterCommand<ForceHeal>(ForceHeal.Definition, c => ((ForceHeal)c).Run);
        services.AddSingleton<ForceHeal>();
        CommandLineBuilder.RegisterCommand<DumpZipInfo>(DumpZipInfo.Definition, c => ((DumpZipInfo)c).Run);
        services.AddSingleton<DumpZipInfo>();
        // New, read-only archive lister
        CommandLineBuilder.RegisterCommand<ListArchives>(ListArchives.Definition, c => ((ListArchives)c).Run);
        services.AddSingleton<ListArchives>();
        CommandLineBuilder.RegisterCommand<DownloadModlistImages>(DownloadModlistImages.Definition, c => ((DownloadModlistImages)c).Run);
        services.AddSingleton<DownloadModlistImages>();
        CommandLineBuilder.RegisterCommand<CheckTokenStatus>(CheckTokenStatus.Definition, c => ((CheckTokenStatus)c).Run);
        services.AddSingleton<CheckTokenStatus>();
        CommandLineBuilder.RegisterCommand<TestExtract>(TestExtract.Definition, c => ((TestExtract)c).Run);
        services.AddSingleton<TestExtract>();
    }
}