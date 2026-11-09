package cn.ethertransfer.mobile;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;

/**
 * 前台服务：让接收通道在息屏、切到后台时依然可用。
 *
 * 电脑发起的文件/文本传输是“被动入站”，一旦进程被系统冻结，内核虽然还在监听，
 * 应用却无法及时处理；更常见的是 Wi-Fi 进入省电后 SYN 直接被丢弃，
 * 发送端只能看到“无响应/超时”。挂在前台可以避免进程被回收与冻结。
 */
public class KeepAliveService extends Service {
    private static final String CHANNEL = "ethertransfer_keepalive";
    private static final int NOTIFICATION_ID = 1001;

    @Override
    public void onCreate() {
        super.onCreate();
        try {
            NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (Build.VERSION.SDK_INT >= 26 && manager.getNotificationChannel(CHANNEL) == null) {
                NotificationChannel channel = new NotificationChannel(
                        CHANNEL, "传输待命", NotificationManager.IMPORTANCE_LOW);
                channel.setDescription("保持接收通道可用，让电脑随时能找到这台手机");
                channel.setShowBadge(false);
                manager.createNotificationChannel(channel);
            }

            Intent open = new Intent(this, MainActivity.class);
            open.setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP);
            PendingIntent pending = PendingIntent.getActivity(
                    this, 0, open, Build.VERSION.SDK_INT >= 23 ? PendingIntent.FLAG_IMMUTABLE : 0);

            Notification.Builder builder = Build.VERSION.SDK_INT >= 26
                    ? new Notification.Builder(this, CHANNEL)
                    : new Notification.Builder(this);
            Notification notification = builder
                    .setContentTitle("以太传输正在待命")
                    .setContentText("电脑可以随时向这台手机发送文件与文本")
                    .setSmallIcon(android.R.drawable.stat_sys_upload_done)
                    .setContentIntent(pending)
                    .setOngoing(true)
                    .build();

            if (Build.VERSION.SDK_INT >= 29) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
            } else {
                startForeground(NOTIFICATION_ID, notification);
            }
        } catch (Exception ignored) {
            // 通知权限被拒时前台服务可能无法展示通知，但服务本身仍继续运行。
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        return START_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
