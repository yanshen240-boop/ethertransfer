package cn.ethertransfer.mobile.uitest;
import cn.ethertransfer.mobile.ChatStore;
import android.app.*;
import android.content.*;
import android.net.Uri;
import android.os.Bundle;
import android.widget.*;
import java.io.*;
import java.lang.reflect.*;
import java.util.*;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * v3 双页 UI 断言：融合输入条状态机 + 会话存储往返。
 * 依赖 MainActivity 暴露的字段：selected/selectedFolders/sizeCache/busy/textInput，
 * 以及 sendButton/clearButton/attachSummary/threadList/currentThreadKey()/rebuildThread()。
 */
public class UiChecks extends Instrumentation {
    private Activity screen;
    private Object field(String name) throws Exception {
        Field f=screen.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(screen);
    }
    private void invoke(String name, Object... args) throws Exception {
        Class[] types=new Class[args.length];
        for(int i=0;i<args.length;i++)types[i]=args[i].getClass();
        Method m=screen.getClass().getDeclaredMethod(name,types);m.setAccessible(true);m.invoke(screen,args);
    }
    private void check(boolean value,String message) { if(!value) throw new AssertionError(message); }
    @Override public void onCreate(Bundle args) { super.onCreate(args);start(); }
    @Override public void onStart() {
        Bundle result=new Bundle();
        try {
            screen=startActivitySync(new Intent().setClassName("cn.ethertransfer.mobile","cn.ethertransfer.mobile.MainActivity").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            final Throwable[] failure={null};
            File original=new File(getTargetContext().getCacheDir(),"ui-clear-check.txt");
            try(FileOutputStream out=new FileOutputStream(original)){out.write(new byte[]{1,2,3});}
            runOnMainSync(() -> {
                try {
                    // —— 融合输入条状态机 ——
                    List<Uri> selected=(List<Uri>)field("selected");
                    Button clear=(Button)field("clearButton"),send=(Button)field("sendButton");
                    List<Uri> folders=(List<Uri>)field("selectedFolders");
                    Map<Uri,Long> sizeCache=(Map<Uri,Long>)field("sizeCache");
                    AtomicBoolean busy=(AtomicBoolean)field("busy");
                    EditText input=(EditText)field("textInput");
                    check(!clear.isEnabled(),"Clear must be disabled for an empty composer");
                    check(!send.isEnabled(),"Send must be disabled without device, attachment or text");
                    selected.add(Uri.fromFile(original));sizeCache.put(Uri.fromFile(original),3L);invoke("updateSelection");
                    check(clear.isEnabled(),"Clear must enable after adding a file");
                    TextView summary=(TextView)field("attachSummary");
                    check(summary.getText().toString().contains("1 项"),"Selected summary missing");
                    folders.add(Uri.parse("content://com.android.externalstorage.documents/tree/primary%3AUiTest"));
                    busy.set(true);invoke("updateActions");
                    check(!clear.isEnabled()&&!send.isEnabled(),"Actions must lock during a transfer");
                    busy.set(false);invoke("updateActions");
                    clear.performClick();
                    check(selected.isEmpty()&&folders.isEmpty(),"Clear did not remove files and folders");
                    check(!clear.isEnabled(),"Empty state did not reset actions");
                    check(summary.getText().toString().contains("支持文件"),"Empty-state summary missing");
                    check(original.isFile()&&original.length()==3,"Clear must not delete the actual file");
                    // 文本也能点亮发送键（无设备时仍禁用）
                    input.setText("hello");invoke("updateActions");
                    check(!send.isEnabled(),"Send must require an online device even with text");

                    // —— 会话存储往返 ——
                    String device="uitest-"+System.currentTimeMillis();
                    ChatStore.Msg first=ChatStore.Msg.text(true,"你好，安卓",ChatStore.SENT);
                    ChatStore.Msg fileMsg=ChatStore.Msg.file(false,"报告.pdf",2048,ChatStore.RECEIVED);
                    ChatStore.append(getTargetContext(),device,first);
                    ChatStore.append(getTargetContext(),device,fileMsg);
                    List<ChatStore.Msg> loaded=ChatStore.load(getTargetContext(),device);
                    check(loaded.size()==2,"Messages must persist");
                    check("你好，安卓".equals(loaded.get(0).text)&&loaded.get(0).out,"Text round-trip mismatch");
                    check("报告.pdf".equals(loaded.get(1).fileName)&&loaded.get(1).size==2048,"File round-trip mismatch");
                    check("2.0 KB".equals(loaded.get(1).sizeText()),"Size formatting mismatch: "+loaded.get(1).sizeText());
                    ChatStore.updateState(getTargetContext(),device,first.msgId,ChatStore.FAILED);
                    check(ChatStore.FAILED.equals(ChatStore.load(getTargetContext(),device).get(0).state),"State update missing");
                    check("发送失败 · 点此重试".equals(ChatStore.load(getTargetContext(),device).get(0).stateText()),"Failed text mismatch");
                    int removed=ChatStore.deleteMessages(getTargetContext(),device,Collections.singletonList(fileMsg.msgId));
                    check(removed==1&&ChatStore.load(getTargetContext(),device).size()==1,"Delete failed");
                    // 只清理测试设备的记录，绝不动真机上的用户消息
                    int cleared=ChatStore.clear(getTargetContext(),device,0);
                    check(cleared==1,"Clear failed");
                    check(ChatStore.load(getTargetContext(),device).isEmpty(),"Store must be empty after clear");
                    check(ChatStore.sizeText(1500*1024*1024L).contains("GB"),"GB formatting mismatch");
                } catch(Throwable t){failure[0]=t;}
            });
            if(failure[0]!=null)throw new RuntimeException(failure[0]);
            result.putString("stream","PASS: composer state machine, transfer locking, chat store round-trip, clear-all");
            finish(Activity.RESULT_OK,result);
        }catch(Throwable t){result.putString("stream","FAIL: "+t.toString());finish(Activity.RESULT_CANCELED,result);}
    }
}
