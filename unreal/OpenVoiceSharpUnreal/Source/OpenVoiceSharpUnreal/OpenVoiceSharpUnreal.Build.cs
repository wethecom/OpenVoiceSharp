using UnrealBuildTool;

public class OpenVoiceSharpUnreal : ModuleRules
{
    public OpenVoiceSharpUnreal(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

        PublicDependencyModuleNames.AddRange(
            new[]
            {
                "Core",
                "CoreUObject",
                "Engine",
                "Sockets",
                "Networking"
            });

        PrivateDependencyModuleNames.AddRange(
            new[]
            {
                "Projects"
            });
    }
}
