// What iOS says about the device's state, for the logs: its thermal state, which Unity's player
// acts on without telling scripts, and the screen's maximum frame rate. DeviceState.cs is the C#
// side.

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>

// NSProcessInfoThermalState: 0 nominal, 1 fair, 2 serious, 3 critical.
extern "C" int CardboardXR_ThermalState()
{
    return (int)[NSProcessInfo processInfo].thermalState;
}

extern "C" int CardboardXR_ScreenMaximumFramesPerSecond()
{
    return (int)[UIScreen mainScreen].maximumFramesPerSecond;
}
