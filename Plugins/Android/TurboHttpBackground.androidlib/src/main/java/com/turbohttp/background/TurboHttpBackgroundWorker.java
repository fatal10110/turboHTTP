package com.turbohttp.background;

import android.content.Context;

import androidx.work.Worker;
import androidx.work.WorkerParameters;

/**
 * Placeholder WorkManager job for a request the managed side queued for deferred replay. Replay
 * itself is orchestrated in C# from an in-memory queue of the process that enqueued the job, so
 * this worker cannot replay anything and ends on its first run (#940): retrying would reschedule
 * the job forever (WorkManager persists it across reboots) after that process died. It succeeds
 * while the enqueuing process is alive and fails for an orphan a dead process left behind.
 */
public final class TurboHttpBackgroundWorker extends Worker {
    static final String KEY_DEDUPE = "dedupeKey";

    public TurboHttpBackgroundWorker(Context appContext, WorkerParameters workerParams) {
        super(appContext, workerParams);
    }

    @Override
    public Result doWork() {
        String dedupeKey = getInputData().getString(KEY_DEDUPE);
        boolean ownedByThisProcess = TurboHttpBackgroundPlugin.reportDeferredWorkStatus(dedupeKey, "COMPLETED");
        // Terminal either way: never Result.retry().
        return ownedByThisProcess ? Result.success() : Result.failure();
    }
}
