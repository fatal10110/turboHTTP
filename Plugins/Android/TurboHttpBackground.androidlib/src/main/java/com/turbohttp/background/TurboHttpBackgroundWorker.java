package com.turbohttp.background;

import android.content.Context;

import androidx.work.Worker;
import androidx.work.WorkerParameters;

/**
 * Placeholder WorkManager job for a request the managed side queued for deferred replay. Replay
 * itself is orchestrated in C# while the process is alive; after a cold start the worker asks
 * WorkManager to retry so the host app can rehydrate its policy and state first.
 */
public final class TurboHttpBackgroundWorker extends Worker {
    static final String KEY_DEDUPE = "dedupeKey";

    public TurboHttpBackgroundWorker(Context appContext, WorkerParameters workerParams) {
        super(appContext, workerParams);
    }

    @Override
    public Result doWork() {
        String dedupeKey = getInputData().getString(KEY_DEDUPE);
        TurboHttpBackgroundPlugin.reportDeferredWorkStatus(dedupeKey, "RETRY");
        return Result.retry();
    }
}
