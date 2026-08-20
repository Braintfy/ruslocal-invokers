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
                String id = field(line, "\"id\":\"");
                String sha = field(line, "\"source_sha256\":\"");
                String text = field(line, "\"translation\":\"");
                if (id == null || sha == null || text == null) continue;
                overlay.byId.put(Long.parseUnsignedLong(id, 16), new Record(sha, unescape(text)));
            }
        }
        return overlay;
    }

    /** Reads one flat JSON string field. The catalog is machine-written, so the shape is fixed. */
    private static String field(String line, String key) {
        int start = line.indexOf(key);
        if (start < 0) return null;
        start += key.length();
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
     * Applies the catalog to the Ukrainian slot, using English only to verify each translation is
     * still current. A record whose source changed is skipped, so the game keeps official text
     * instead of showing a translation written for a different sentence.
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
            Record record = byId.get(entry.keyHash);
            if (record == null) continue;
            String source = englishByHash.get(entry.keyHash);
            if (source == null) continue;
            if (!hex(digest.digest(source.getBytes(StandardCharsets.UTF_8))).equalsIgnoreCase(record.sourceSha256)) {
                continue;
            }
            entry.value = record.translation;
            applied++;
        }
        return applied;
    }

    /** Every entry the catalog does not cover falls back to the official English text. */
    static int fillEnglishFallback(Loc1 english, Loc1 target, java.util.Set<Long> translated) {
        Map<Long, String> englishByHash = new HashMap<>(english.entries.size() * 2);
        for (Loc1.Entry e : english.entries) {
            if (!e.wasNull) englishByHash.put(e.keyHash, e.value);
        }
        int filled = 0;
        for (Loc1.Entry entry : target.entries) {
            if (entry.wasNull || translated.contains(entry.keyHash)) continue;
            String source = englishByHash.get(entry.keyHash);
            if (source != null && !source.equals(entry.value)) {
                entry.value = source;
                filled++;
            }
        }
        return filled;
    }

    java.util.Set<Long> idsAppliedTo(Loc1 english, Loc1 target) throws NoSuchAlgorithmException {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        Map<Long, String> englishByHash = new HashMap<>(english.entries.size() * 2);
        for (Loc1.Entry e : english.entries) {
            if (!e.wasNull) englishByHash.put(e.keyHash, e.value);
        }
        java.util.Set<Long> ids = new java.util.HashSet<>();
        for (Loc1.Entry entry : target.entries) {
            Record record = byId.get(entry.keyHash);
            if (record == null || entry.wasNull) continue;
            String source = englishByHash.get(entry.keyHash);
            if (source == null) continue;
            if (hex(digest.digest(source.getBytes(StandardCharsets.UTF_8))).equalsIgnoreCase(record.sourceSha256)) {
                ids.add(entry.keyHash);
            }
        }
        return ids;
    }

    static String hex(byte[] bytes) {
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) sb.append(String.format("%02X", b));
        return sb.toString();
    }
}
