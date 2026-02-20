#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#include <math.h>

static void TurboHttpRunOnMainThreadSync(dispatch_block_t block)
{
    if (block == nil)
        return;

    if ([NSThread isMainThread])
    {
        block();
        return;
    }

    dispatch_sync(dispatch_get_main_queue(), block);
}

extern "C" int turbohttp_begin_background_task(const char *taskName)
{
    __block int result = -1;

    TurboHttpRunOnMainThreadSync(^
    {
        UIApplication *app = [UIApplication sharedApplication];
        if (app == nil)
            return;

        NSString *name = nil;
        if (taskName != NULL)
            name = [NSString stringWithUTF8String:taskName];
        if (name == nil || name.length == 0)
            name = @"TurboHTTP.Request";

        __block UIBackgroundTaskIdentifier taskId = UIBackgroundTaskInvalid;
        taskId = [app beginBackgroundTaskWithName:name expirationHandler:^
        {
            UIBackgroundTaskIdentifier expiredTaskId = taskId;
            if (expiredTaskId != UIBackgroundTaskInvalid)
            {
                [app endBackgroundTask:expiredTaskId];
            }
        }];

        if (taskId != UIBackgroundTaskInvalid)
            result = (int)taskId;
    });

    return result;
}

extern "C" void turbohttp_end_background_task(int taskId)
{
    if (taskId < 0)
        return;

    TurboHttpRunOnMainThreadSync(^
    {
        UIApplication *app = [UIApplication sharedApplication];
        if (app == nil)
            return;

        [app endBackgroundTask:(UIBackgroundTaskIdentifier)taskId];
    });
}

extern "C" double turbohttp_background_time_remaining(void)
{
    __block double remainingSeconds = 0.0;

    TurboHttpRunOnMainThreadSync(^
    {
        UIApplication *app = [UIApplication sharedApplication];
        if (app == nil)
            return;

        NSTimeInterval remaining = app.backgroundTimeRemaining;
        if (!isfinite(remaining) || remaining < 0)
            remaining = 0;

        remainingSeconds = remaining;
    });

    return remainingSeconds;
}
