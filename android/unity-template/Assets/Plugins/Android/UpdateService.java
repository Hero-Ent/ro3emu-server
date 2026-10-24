package com.ro3emu.updater;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;

/**
 * Keeps the app alive while the game data downloads (android/updater/UpdateScreen.cs): without a foreground service
 * Android freezes or kills an app soon after it leaves the screen, and the CPU and Wi-Fi sleep with the screen off.
 * The download itself runs in the game's C# threads; this only holds the process, the locks and the notification.
 */
public class UpdateService extends Service {
    private static final String CHANNEL = "ro3_update";
    private static final int ID = 3301, DONE_ID = 3302;
    private static String title = "Hero Saga", text = "";
    private static int percent = -1;

    private PowerManager.WakeLock wake;
    private WifiManager.WifiLock wifi;

    public static void start(Context context, String t, String body) {
        title = t;
        text = body;
        percent = -1;
        Intent intent = new Intent(context, UpdateService.class);
        if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent);
        else context.startService(intent);
    }

    public static void update(Context context, String body, int p) {
        text = body;
        percent = p;
        NotificationManager nm = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null) nm.notify(ID, build(context));
    }

    public static void stop(Context context) {
        context.stopService(new Intent(context, UpdateService.class));
    }

    /** Stops the service and leaves a dismissible notice (the download may end while the app is in the background). */
    public static void finish(Context context, String t, String body) {
        stop(context);
        NotificationManager nm = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm == null) return;
        title = t;
        text = body;
        Notification.Builder b = builder(context);
        nm.notify(DONE_ID, b.setSmallIcon(context.getApplicationInfo().icon).setContentTitle(t).setContentText(body)
                .setAutoCancel(true).build());
    }

    public static void clearDone(Context context) {
        NotificationManager nm = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null) nm.cancel(DONE_ID);
    }

    private static Notification.Builder builder(Context context) {
        Notification.Builder b;
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationManager nm = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null && nm.getNotificationChannel(CHANNEL) == null) {
                nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Download do jogo", NotificationManager.IMPORTANCE_LOW));
            }
            b = new Notification.Builder(context, CHANNEL);
        } else {
            b = new Notification.Builder(context);
        }
        Intent open = context.getPackageManager().getLaunchIntentForPackage(context.getPackageName());
        if (open != null) {
            b.setContentIntent(PendingIntent.getActivity(context, 0, open, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT));
        }
        return b;
    }

    private static Notification build(Context context) {
        return builder(context).setSmallIcon(context.getApplicationInfo().icon)
                .setContentTitle(title)
                .setContentText(text)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setProgress(100, Math.max(0, percent), percent < 0)
                .build();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        Notification n = build(this);
        if (Build.VERSION.SDK_INT >= 29) startForeground(ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        else startForeground(ID, n);
        if (wake == null) {
            PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
            wake = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "ro3:update");
            wake.acquire(6 * 60 * 60 * 1000L);
        }
        if (wifi == null) {
            WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wm != null) {
                wifi = wm.createWifiLock(Build.VERSION.SDK_INT >= 29 ? WifiManager.WIFI_MODE_FULL_HIGH_PERF : WifiManager.WIFI_MODE_FULL, "ro3:update");
                wifi.acquire();
            }
        }
        return START_NOT_STICKY;
    }

    @Override
    public void onDestroy() {
        if (wake != null && wake.isHeld()) wake.release();
        if (wifi != null && wifi.isHeld()) wifi.release();
        wake = null;
        wifi = null;
        if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE);
        else stopForeground(true);
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
