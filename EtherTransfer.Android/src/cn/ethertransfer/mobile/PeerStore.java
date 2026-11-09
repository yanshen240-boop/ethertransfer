package cn.ethertransfer.mobile;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * 已知设备的小型索引（id / 名称 / 地址 / 系统 / 最近在线）。
 * <p>会话列表需要给「已离线但聊过」的设备显示名字，而发现结果里没有离线设备，
 * 因此把每次见到的设备落一份到 SharedPreferences（只存元数据，不碰消息）。</p>
 */
final class PeerStore {
    private static final String KEY = "knownPeers";

    static final class Known {
        String id = "";
        String name = "";
        String ip = "";
        String os = "PC";
        long lastSeen;
    }

    private PeerStore() { }

    private static SharedPreferences prefs(Context c) {
        return c.getSharedPreferences("peers", Context.MODE_PRIVATE);
    }

    static synchronized void remember(Context c, String id, String name, String ip, String os) {
        if (id == null || id.isEmpty()) return;
        Map<String, Known> all = all(c);
        Known known = all.get(id);
        if (known == null) known = new Known();
        known.id = id;
        if (name != null && !name.isEmpty()) known.name = name;
        if (ip != null && !ip.isEmpty()) known.ip = ip;
        if (os != null && !os.isEmpty()) known.os = os;
        known.lastSeen = System.currentTimeMillis();
        all.put(id, known);
        save(c, all);
    }

    static synchronized Known get(Context c, String id) {
        if (id == null) return null;
        return all(c).get(id);
    }

    static synchronized Map<String, Known> all(Context c) {
        Map<String, Known> map = new LinkedHashMap<>();
        String raw = prefs(c).getString(KEY, "");
        if (raw == null || raw.isEmpty()) return map;
        try {
            JSONArray array = new JSONArray(raw);
            for (int i = 0; i < array.length(); i++) {
                JSONObject o = array.optJSONObject(i);
                if (o == null) continue;
                Known k = new Known();
                k.id = o.optString("id", "");
                k.name = o.optString("name", "");
                k.ip = o.optString("ip", "");
                k.os = o.optString("os", "PC");
                k.lastSeen = o.optLong("lastSeen", 0);
                if (!k.id.isEmpty()) map.put(k.id, k);
            }
        } catch (Exception ignored) { }
        return map;
    }

    static synchronized void forget(Context c, String id) {
        Map<String, Known> all = all(c);
        all.remove(id);
        save(c, all);
    }

    private static void save(Context c, Map<String, Known> all) {
        JSONArray array = new JSONArray();
        List<Known> list = new ArrayList<>(all.values());
        for (Known k : list) {
            JSONObject o = new JSONObject();
            try {
                o.put("id", k.id);
                o.put("name", k.name);
                o.put("ip", k.ip);
                o.put("os", k.os);
                o.put("lastSeen", k.lastSeen);
                array.put(o);
            } catch (Exception ignored) { }
        }
        prefs(c).edit().putString(KEY, array.toString()).apply();
    }
}
