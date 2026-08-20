package ru.invokers.patcher;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.HashMap;
import java.util.Map;

/**
 * The public translation catalog: one JSON object per line, carrying a string id, the SHA-256 of the
 * English source it was written against, and the Russian text. It deliberately holds no English
 * text, so a translation is only used when the current source still hashes to the recorded value.
 */
final class Overlay {
    private final Map<Long, Record> byId = new HashMap<>(48000);

    static final class Record {
        final String sourceSha256;
        final String translation;

        Record(String sourceSha256, String translation) {
            this.sourceSha256 = sourceSha256;
            this.translation = translation;
        }
    }

    int size() { return byId.size(); }

    static Overlay read(InputStream stream) throws IOException {
        Overlay overlay = new Overlay();
        try (BufferedReader reader = new BufferedReader(
                new InputStreamReader(stream, StandardCharsets.UTF_8), 1 << 16)) {
            String line;
            while ((line = reader.readLine()) != null) {
                if (line.isEmpty()) continue;
                String id = field(line, "id");
                String sha = field(line, "source_sha256");
                String text = field(line, "translation");
                if (id == null || sha == null || text == null) continue;
                overlay.byId.put(Long.parseUnsignedLong(id, 16), new Record(sha, unescape(text)));
            }
        }
        return overlay;
    }

    /**
     * Reads one flat JSON string field. The catalog is machine-written, but whether it carries a
     * space after the colon has changed between generations of the tooling, so the separator is
     * skipped rather than assumed.
     */
    private static String field(String line, String key) {
        String needle = "\"" + key + "\"";
        int start = line.indexOf(needle);
        if (start < 0) return null;
        start += needle.length();
        while (start < line.length() && Character.isWhitespace(line.charAt(start))) start++;
        if (start >= line.length() || line.charAt(start) != ':') return null;
        start++;
        while (start < line.length() && Character.isWhitespace(line.charAt(start))) start++;
        if (start >= line.length() || line.charAt(start) != '"') return null;
        start++;
        StringBuilder sb = new StringBuilder();
        for (int i = start; i < line.length(); i++) {
            char c = line.charAt(i);
            if (c == '\\') {
                if (i + 1 >= line.length()) return null;
                sb.append(c).append(line.charAt(++i));
                continue;
            }
            if (c == '"') return sb.toString();
            sb.append(c);
        }
        return null;
    }

    private static String unescape(String raw) {
        StringBuilder sb = new StringBuilder(raw.length());
        for (int i = 0; i < raw.length(); i++) {
            char c = raw.charAt(i);
            if (c != '\\' || i + 1 >= raw.length()) { sb.append(c); continue; }
            char next = raw.charAt(++i);
            switch (next) {
                case 'n': sb.append('\n'); break;
                case 'r': sb.append('\r'); break;
                case 't': sb.append('\t'); break;
                case 'b': sb.append('\b'); break;
                case 'f': sb.append('\f'); break;
                case '"': sb.append('"'); break;
                case '\\': sb.append('\\'); break;
                case '/': sb.append('/'); break;
                case 'u':
                    if (i + 4 < raw.length()) {
                        sb.append((char) Integer.parseInt(raw.substring(i + 1, i + 5), 16));
                        i += 4;
                    }
                    break;
                default: sb.append('\\').append(next);
            }
        }
        return sb.toString();
    }

    /**
     * Rewrites the Ukrainian slot in a single pass.
     *
     * A translation is used only while the English source still hashes to the value it was written
     * against, so a sentence the game reworded keeps its official text instead of a translation
     * meant for something else. Everything the catalog does not cover falls back to English, which
     * is what the rest of the project builds too.
     *
     * One pass matters here: this runs on a phone, and hashing every string twice over a catalog of
     * forty thousand entries is the difference between seconds and minutes.
     */
    int apply(Loc1 english, Loc1 target) throws NoSuchAlgorithmException {
        Map<Long, String> englishByHash = new HashMap<>(english.entries.size() * 2);
        for (Loc1.Entry e : english.entries) {
            if (!e.wasNull) englishByHash.put(e.keyHash, e.value);
        }

        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        int applied = 0;
        for (Loc1.Entry entry : target.entries) {
            if (entry.wasNull) continue;
            String source = englishByHash.get(entry.keyHash);
            if (source == null) continue;

            Record record = byId.get(entry.keyHash);
            if (record != null
                    && hex(digest.digest(source.getBytes(StandardCharsets.UTF_8)))
                            .equalsIgnoreCase(record.sourceSha256)) {
                entry.value = record.translation;
                applied++;
            } else if (!source.equals(entry.value)) {
                entry.value = source;
            }
        }
        return applied;
    }

    private static final char[] HEX = "0123456789ABCDEF".toCharArray();

    /** Hand-rolled because String.format costs whole minutes over a catalog this size. */
    static String hex(byte[] bytes) {
        char[] out = new char[bytes.length * 2];
        for (int i = 0; i < bytes.length; i++) {
            int v = bytes[i] & 0xFF;
            out[i * 2] = HEX[v >>> 4];
            out[i * 2 + 1] = HEX[v & 0x0F];
        }
        return new String(out);
    }
}
