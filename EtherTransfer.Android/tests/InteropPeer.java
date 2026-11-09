package cn.ethertransfer.mobile;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.regex.*;

/** Runs the exact Android framing/folder parser against the C# sender and receiver. */
public class InteropPeer {
    static void require(boolean value) { if (!value) throw new AssertionError(); }
    static long size(String json) {
        Matcher m = Pattern.compile("\"Size\"\\s*:\\s*(\\d+)").matcher(json);
        if (!m.find()) throw new AssertionError(json);
        return Long.parseLong(m.group(1));
    }
    static void checkPayload(InputStream in, long length) throws IOException {
        for (long i = 0; i < length; i++) require(in.read() == (int)(i % 251));
    }
    public static void main(String[] args) throws Exception {
        if (args[0].equals("self")) { self(); return; }
        if (args[0].equals("receive")) {
            try (ServerSocket server = new ServerSocket(0, 1, InetAddress.getLoopbackAddress())) {
                System.out.println(server.getLocalPort()); System.out.flush();
                try (Socket socket = server.accept()) {
                    socket.setSoTimeout(10000); InputStream in = socket.getInputStream(); OutputStream out = socket.getOutputStream();
                    require(Wire.message(in).contains("TRANSFER_REQUEST"));
                    Wire.message(out, "{\"Type\":\"TRANSFER_RESPONSE\",\"Accepted\":true}");
                    int files = 0;
                    while (true) {
                        String marker = Wire.message(in);
                        if (marker.contains("TRANSFER_END")) break;
                        if (marker.contains("FILE_BEGIN")) {
                            String meta = Wire.message(in); checkPayload(in, size(meta)); files++;
                        } else if (marker.contains("FOLDER_BEGIN")) {
                            Wire.message(in);
                            files += Wire.folder(in, (path, length) -> checkPayload(in, length));
                        } else throw new AssertionError(marker);
                    }
                    require(files == 2);
                }
            }
        } else {
            try (Socket socket = new Socket("127.0.0.1", Integer.parseInt(args[1]))) {
                socket.setSoTimeout(10000); InputStream in = socket.getInputStream(); OutputStream out = socket.getOutputStream();
                boolean folder=!args[0].equals("send"),empty=args[0].equals("send-empty-folder");
                if(folder) {
                    Wire.message(out,"{\"Type\":\"TRANSFER_REQUEST\",\"SenderName\":\"Android folder test\",\"TotalFiles\":"+(empty?0:2)+",\"TotalSize\":"+(empty?0:65539)+",\"ContainsFolders\":true,\"PayloadFolderCount\":1,\"PayloadFileCount\":0,\"RootElementNames\":[\"中文文件夹\"]}");
                    require(Wire.message(in).contains("true"));
                    Wire.message(out,"{\"Type\":\"FOLDER_BEGIN\"}");
                    Wire.message(out,"{\"RootName\":\"中文文件夹\",\"TotalFiles\":"+(empty?0:2)+",\"TotalSize\":"+(empty?0:65539)+"}");
                    if(!empty) {
                        Wire.folderEntry(out,"中文文件夹/子目录/中文.bin",65539);
                        for(int i=0;i<65539;i++)out.write(i%251);
                        Wire.folderEntry(out,"中文文件夹/empty.txt",0);
                    }
                    Wire.number(out,0,4);
                }else {
                Wire.message(out, "{\"Type\":\"TRANSFER_REQUEST\",\"SenderName\":\"Android test\",\"TotalFiles\":2,\"TotalSize\":65539,\"ContainsFolders\":false,\"PayloadFolderCount\":0,\"PayloadFileCount\":2,\"RootElementNames\":[\"中文.bin\",\"empty.txt\"]}");
                require(Wire.message(in).contains("true"));
                Wire.message(out, "{\"Type\":\"FILE_BEGIN\"}");
                Wire.message(out, "{\"RelativePath\":\"中文.bin\",\"RootName\":\"中文.bin\",\"Size\":65539}");
                for (int i = 0; i < 65539; i++) out.write(i % 251);
                Wire.message(out, "{\"Type\":\"FILE_BEGIN\"}");
                Wire.message(out, "{\"RelativePath\":\"empty.txt\",\"RootName\":\"empty.txt\",\"Size\":0}");
                }
                Wire.message(out, "{\"Type\":\"TRANSFER_END\"}");
            }
        }
        System.out.println("PASS");
    }
    static void checkBytes(InputStream in)throws IOException {require(in.read()==1);require(in.read()==2);require(in.read()==3);}
    static void self() throws Exception {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        Wire.message(out, "中文😀");
        byte[] bytes = out.toByteArray(); require(bytes[0] == 10); require(bytes[1] == 0);
        require(Wire.message(new ByteArrayInputStream(bytes)).equals("中文😀"));
        for (String path : new String[]{"../x", "/x", "C:/x", "a\\b", "a//b", "a/./b", "a\u0000b"}) {
            try { Wire.safePath(path); throw new AssertionError(path); } catch (IOException expected) { }
        }
        require(Wire.safePath("中文/a.txt").length == 2);
        try { Wire.copy(new ByteArrayInputStream(new byte[1]), new ByteArrayOutputStream(), 2); throw new AssertionError(); }
        catch (EOFException expected) { }
        for (byte[] invalid : new byte[][]{new byte[]{-1,-1,-1,-1}, new byte[]{0,0,0,0}, new byte[]{4,0,0,0,1}}) {
            try { Wire.message(new ByteArrayInputStream(invalid)); throw new AssertionError(); } catch (IOException expected) { }
        }
        out.reset(); byte[] path = "folder/空.txt".getBytes(StandardCharsets.UTF_8);
        Wire.number(out, path.length, 4); out.write(path); Wire.number(out, 0, 8);
        Wire.number(out, -1, 8); Wire.number(out, -1, 8); Wire.number(out, 0, 4);
        require(Wire.folder(new ByteArrayInputStream(out.toByteArray()), (p,n) -> require(p.equals("folder/空.txt") && n == 0)) == 1);
        out.reset(); Wire.folderEntry(out,"folder/中文.txt",3);out.write(new byte[]{1,2,3});Wire.number(out,0,4);
        ByteArrayInputStream folderIn=new ByteArrayInputStream(out.toByteArray());
        require(Wire.folder(folderIn,(p,n)->{require(p.equals("folder/中文.txt")&&n==3);checkBytes(folderIn);})==1);
        try{Wire.folderEntry(out,"../escape",0);throw new AssertionError();}catch(IOException expected){}
        try{Wire.folderEntry(out,"safe",-1);throw new AssertionError();}catch(IOException expected){}
        System.out.println("PASS: UTF-8, little endian, invalid frames, paths, truncation, empty folder file");
    }
}
