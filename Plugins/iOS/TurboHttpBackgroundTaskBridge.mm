#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#include <math.h>

// TurboHTTP background networking bridge (experimental, opt-in): Unity links this file only when
// the TURBOHTTP_BACKGROUND_NETWORKING scripting define is set (importer define constraint), the
// same define that compiles the managed IosBackgroundTaskBindings.
//
// Threading: beginBackgroundTaskWithName:expirationHandler: and endBackgroundTask: are documented
// as safe to call from any thread, so begin/end never hop to the main thread and keep working while
// the Unity player loop is paused (#88). Each managed scope is identified by a cookie the managed
// side allocates and registers before calling begin, so an expiration that fires right away still
// finds its scope. The table below maps cookies to UIKit task ids; whoever removes an entry (end or
// the expiration handler) ends that task, so every begun task is ended exactly once. No lock is held
// across a UIKit call.

typedef void (*TurboHttpBackgroundExpiredCallback)(int cookie);

static TurboHttpBackgroundExpiredCallback s_expiredCallback = NULL;

static NSMutableDictionary<NSNumber *, NSNumber *> *TurboHttpBackgroundTasks(void)
{
    static NSMutableDictionary<NSNumber *, NSNumber *> *tasks = nil;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        tasks = [[NSMutableDictionary alloc] init];
    });
    return tasks;
}

// Cookies whose expiration handler ran before begin stored the task id (begin then ends the task).
// Guarded by the tasks table lock; an entry is removed by the begin call that owns the cookie.
static NSMutableSet<NSNumber *> *TurboHttpExpiredBeforeBegin(void)
{
    static NSMutableSet<NSNumber *> *expired = nil;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        expired = [[NSMutableSet alloc] init];
    });
    return expired;
}

static UIBackgroundTaskIdentifier TurboHttpTakeTask(int cookie, BOOL fromExpiration)
{
    NSMutableDictionary<NSNumber *, NSNumber *> *tasks = TurboHttpBackgroundTasks();
    UIBackgroundTaskIdentifier taskId = UIBackgroundTaskInvalid;
    @synchronized (tasks)
    {
        NSNumber *key = @(cookie);
        NSNumber *value = tasks[key];
        if (value != nil)
        {
            taskId = (UIBackgroundTaskIdentifier)value.unsignedIntegerValue;
            [tasks removeObjectForKey:key];
        }
        else if (fromExpiration)
        {
            [TurboHttpExpiredBeforeBegin() addObject:key];
        }
    }
    return taskId;
}

static void TurboHttpBackgroundTaskExpired(int cookie)
{
    // UIKit calls this on the main thread shortly before it suspends the app, so expiry is armed
    // only while the app is in the background (#87). The managed callback cancels the scope's
    // expiration token; the task is ended here because UIKit requires it before the handler returns.
    UIBackgroundTaskIdentifier taskId = TurboHttpTakeTask(cookie, YES);
    if (taskId == UIBackgroundTaskInvalid)
        return;

    TurboHttpBackgroundExpiredCallback callback = __atomic_load_n(&s_expiredCallback, __ATOMIC_ACQUIRE);
    if (callback != NULL)
        callback(cookie);

    [[UIApplication sharedApplication] endBackgroundTask:taskId];
}

extern "C" void turbohttp_set_background_expiration_callback(TurboHttpBackgroundExpiredCallback callback)
{
    __atomic_store_n(&s_expiredCallback, callback, __ATOMIC_RELEASE);
}

extern "C" int turbohttp_begin_background_task(const char *taskName, int cookie)
{
    UIApplication *app = [UIApplication sharedApplication];
    if (app == nil)
        return 0;

    NSString *name = nil;
    if (taskName != NULL)
        name = [NSString stringWithUTF8String:taskName];
    if (name == nil || name.length == 0)
        name = @"TurboHTTP.Request";

    UIBackgroundTaskIdentifier taskId = [app beginBackgroundTaskWithName:name expirationHandler:^
    {
        TurboHttpBackgroundTaskExpired(cookie);
    }];

    if (taskId == UIBackgroundTaskInvalid)
        return 0;

    NSMutableDictionary<NSNumber *, NSNumber *> *tasks = TurboHttpBackgroundTasks();
    NSNumber *key = @(cookie);
    BOOL expiredAlready = NO;
    @synchronized (tasks)
    {
        NSMutableSet<NSNumber *> *expired = TurboHttpExpiredBeforeBegin();
        if ([expired containsObject:key])
        {
            [expired removeObject:key];
            expiredAlready = YES;
        }
        else
        {
            tasks[key] = @(taskId);
        }
    }

    if (expiredAlready)
    {
        // No time left: end the task now and report failure, so the request runs with foreground semantics.
        [app endBackgroundTask:taskId];
        return 0;
    }

    return 1;
}

extern "C" void turbohttp_end_background_task(int cookie)
{
    UIBackgroundTaskIdentifier taskId = TurboHttpTakeTask(cookie, NO);
    if (taskId == UIBackgroundTaskInvalid)
        return;

    [[UIApplication sharedApplication] endBackgroundTask:taskId];
}

extern "C" double turbohttp_background_time_remaining(void)
{
    // backgroundTimeRemaining is main-thread only. The managed side reads it only through
    // IBackgroundExecutionScope.RemainingBudget before expiry (never on the request path).
    __block double remainingSeconds = -1.0;
    dispatch_block_t read = ^
    {
        UIApplication *app = [UIApplication sharedApplication];
        if (app == nil)
            return;

        NSTimeInterval remaining = app.backgroundTimeRemaining;
        // In the foreground UIKit reports DBL_MAX: no limit. -1 tells the managed side "unlimited".
        if (!isfinite(remaining) || remaining > 1.0e6)
            remaining = -1.0;
        else if (remaining < 0)
            remaining = 0;

        remainingSeconds = remaining;
    };

    if ([NSThread isMainThread])
        read();
    else
        dispatch_sync(dispatch_get_main_queue(), read);

    return remainingSeconds;
}
