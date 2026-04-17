#pragma once

#include "Modules/ModuleManager.h"

class FOpenVoiceSharpUnrealModule : public IModuleInterface
{
public:
    virtual void StartupModule() override;
    virtual void ShutdownModule() override;
};
