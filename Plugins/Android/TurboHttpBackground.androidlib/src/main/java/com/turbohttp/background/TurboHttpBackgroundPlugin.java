package com.turbohttp.background;

import android.content.Context;

import androidx.work.Data;
import androidx.work.ExistingWorkPolicy;
import androidx.work.OneTimeWorkRequest;
import androidx.work.WorkManager;

import java.util.concurrent.ConcurrentHashMap;

/**
 * Static entry points the managed AndroidBackgroundWorkBridge calls through JNI. Every method is
 * safe to call from any thread and never blocks on WorkManager futures.
 */
public final class TurboHttpBackgroundPlugin {
    /** Tag on every deferred job, so a new process can cancel the ones a dead process left (#940). */
    static final String DEFERRED_WORK_TAG = "turbohttp-deferred";

    private static final ConcurrentHashMap<String, Long> activeGuards = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, String> statusByKey = new ConcurrentHashMap<>();
    private static final Object initLock = new Object();
    private static volatile Context applicationContext;
    // Guarded by initLock: set once the orphan cancel of this process has been issued.
    private static boolean orphansCleared;

    private TurboHttpBackgroundPlugin() {
    }

    public static boolean initialize(Context context) {
        if (context == null) {
            return false;
        }

        Context app = context.getApplicationContext();
        Context resolved = app != null ? app : context;
        synchronized (initLock) {
            if (!orphansCleared) {
                // The managed replay queue of a new process is empty, so every tagged job left by an
                // earlier process is an orphan nothing will replay or cancel: cancel them once (#940).
                // Issued before applicationContext is published, and enqueueDeferredWork needs it, so
                // no job of this process is enqueued first; WorkManager runs its operations in
                // submission order on one serial executor. The Operation is not awaited.
                WorkManager.getInstance(resolved).cancelAllWorkByTag(DEFERRED_WORK_TAG);
                orphansCleared = true;
            }

            applicationContext = resolved;
        }
        return true;
    }

    public static void beginInProcessGuard(String scopeId, int graceMs) {
        if (scopeId != null) {
            activeGuards.put(scopeId, System.currentTimeMillis() + graceMs);
        }
    }

    public static void endInProcessGuard(String scopeId) {
        if (scopeId != null) {
            activeGuards.remove(scopeId);
        }
    }

    public static boolean enqueueDeferredWork(String dedupeKey) {
        Context context = applicationContext;
        if (context == null || dedupeKey == null || dedupeKey.isEmpty()) {
            return false;
        }

        OneTimeWorkRequest request = new OneTimeWorkRequest.Builder(TurboHttpBackgroundWorker.class)
            .addTag(DEFERRED_WORK_TAG)
            .setInputData(new Data.Builder().putString(TurboHttpBackgroundWorker.KEY_DEDUPE, dedupeKey).build())
            .build();

        // enqueueUniqueWork returns an Operation; it is not awaited, so the caller never blocks.
        WorkManager.getInstance(context)
            .enqueueUniqueWork(uniqueName(dedupeKey), ExistingWorkPolicy.KEEP, request);
        statusByKey.put(dedupeKey, "ENQUEUED");
        return true;
    }

    public static boolean cancelDeferredWork(String dedupeKey) {
        Context context = applicationContext;
        if (dedupeKey == null) {
            return false;
        }

        statusByKey.remove(dedupeKey);
        if (context == null) {
            return false;
        }

        WorkManager.getInstance(context).cancelUniqueWork(uniqueName(dedupeKey));
        return true;
    }

    /** Last status this process observed for the key; never waits on WorkManager. */
    public static String queryDeferredWorkStatus(String dedupeKey) {
        if (dedupeKey == null) {
            return "UNKNOWN";
        }

        String status = statusByKey.get(dedupeKey);
        return status != null ? status : "UNKNOWN";
    }

    public static int currentGuardCount() {
        return activeGuards.size();
    }

    /** Records the status of a key this process enqueued; {@code false} for any other key. */
    static boolean reportDeferredWorkStatus(String dedupeKey, String status) {
        // Only keys this process enqueued are tracked, so the map stays bounded by the managed queue.
        if (dedupeKey == null || dedupeKey.isEmpty() || status == null) {
            return false;
        }

        return statusByKey.replace(dedupeKey, status) != null;
    }

    private static String uniqueName(String dedupeKey) {
        return "turbohttp:" + dedupeKey;
    }
}
