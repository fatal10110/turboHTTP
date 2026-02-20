package com.turbohttp.background

import android.content.Context
import androidx.work.Worker
import androidx.work.WorkerParameters

class TurboHttpBackgroundWorker(
    appContext: Context,
    workerParams: WorkerParameters
) : Worker(appContext, workerParams) {

    companion object {
        @Volatile
        var applicationContext: Context? = null
    }

    override fun doWork(): Result {
        Companion.applicationContext = this.applicationContext
        val dedupeKey = inputData.getString("dedupeKey").orEmpty()
        TurboHttpBackgroundPlugin.reportDeferredWorkStatus(dedupeKey, "RUNNING")
        // Managed replay is orchestrated on C# side when process is alive.
        // If app is cold-started, this worker keeps work deterministic by
        // returning retry so host app can rehydrate policy/state.
        TurboHttpBackgroundPlugin.reportDeferredWorkStatus(dedupeKey, "RETRY")
        return Result.retry()
    }
}
