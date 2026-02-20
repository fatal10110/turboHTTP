package com.turbohttp.background

import android.content.Context
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkInfo
import androidx.work.WorkManager
import androidx.work.workDataOf
import com.unity3d.player.UnityPlayer
import java.util.concurrent.ConcurrentHashMap

object TurboHttpBackgroundPlugin {
    private val activeGuards = ConcurrentHashMap<String, Long>()
    private val statusByKey = ConcurrentHashMap<String, String>()

    @JvmStatic
    fun initialize(activityContext: Context?): Boolean {
        val resolved = activityContext?.applicationContext
            ?: UnityPlayer.currentActivity?.applicationContext
            ?: return false

        TurboHttpBackgroundWorker.applicationContext = resolved
        return true
    }

    @JvmStatic
    fun beginInProcessGuard(scopeId: String, graceMs: Int) {
        activeGuards[scopeId] = System.currentTimeMillis() + graceMs
    }

    @JvmStatic
    fun endInProcessGuard(scopeId: String) {
        activeGuards.remove(scopeId)
    }

    @JvmStatic
    fun enqueueDeferredWork(dedupeKey: String): Boolean {
        val context: Context = TurboHttpBackgroundWorker.applicationContext
            ?: UnityPlayer.currentActivity?.applicationContext
            ?: return false

        TurboHttpBackgroundWorker.applicationContext = context
        val request = OneTimeWorkRequestBuilder<TurboHttpBackgroundWorker>()
            .setInputData(workDataOf("dedupeKey" to dedupeKey))
            .build()

        WorkManager.getInstance(context)
            .enqueueUniqueWork("turbohttp:$dedupeKey", ExistingWorkPolicy.KEEP, request)
        statusByKey[dedupeKey] = "ENQUEUED"
        return true
    }

    @JvmStatic
    fun cancelDeferredWork(dedupeKey: String): Boolean {
        val context: Context = TurboHttpBackgroundWorker.applicationContext
            ?: UnityPlayer.currentActivity?.applicationContext
            ?: return false

        WorkManager.getInstance(context).cancelUniqueWork("turbohttp:$dedupeKey")
        statusByKey[dedupeKey] = "CANCELLED"
        return true
    }

    @JvmStatic
    fun queryDeferredWorkStatus(dedupeKey: String): String {
        val context: Context = TurboHttpBackgroundWorker.applicationContext
            ?: UnityPlayer.currentActivity?.applicationContext
            ?: return statusByKey[dedupeKey] ?: "UNKNOWN"

        val infos = try {
            WorkManager.getInstance(context)
                .getWorkInfosForUniqueWork("turbohttp:$dedupeKey")
                .get()
        } catch (_: Throwable) {
            return statusByKey[dedupeKey] ?: "UNKNOWN"
        }

        if (infos.isNullOrEmpty()) return statusByKey[dedupeKey] ?: "UNKNOWN"

        val state = infos[0].state
        val mapped = when (state) {
            WorkInfo.State.ENQUEUED -> "ENQUEUED"
            WorkInfo.State.RUNNING -> "RUNNING"
            WorkInfo.State.SUCCEEDED -> "SUCCEEDED"
            WorkInfo.State.FAILED -> "FAILED"
            WorkInfo.State.BLOCKED -> "BLOCKED"
            WorkInfo.State.CANCELLED -> "CANCELLED"
        }
        statusByKey[dedupeKey] = mapped
        return mapped
    }

    internal fun reportDeferredWorkStatus(dedupeKey: String, status: String) {
        if (dedupeKey.isNotBlank()) {
            statusByKey[dedupeKey] = status
        }
    }

    @JvmStatic
    fun currentGuardCount(): Int {
        return activeGuards.size
    }
}
