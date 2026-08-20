package ru.invokers.patcher;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/**
 * Reader and writer for the game's LOC1 localization container.
 *
 * The header is preserved byte for byte apart from the declared data length, because the client
 * checks the content identity it carries — GUID, locale id, revisions and content version — against
 * what it expects. Only the string pool is rebuilt.
 */
final class Loc1 {
    static final class Entry {
        final long keyHash;
        final boolean wasNull;
        String value;

        Entry(long keyHash, boolean wasNull, String value) {
            this.keyHash = keyHash;
            this.wasNull = wasNull;
            this.value = value;
        }
    }

    final byte[] header;
    final List<Entry> entries;
    final String contentGuid;
    final String contentVersion;
    final long localeId;

    private Loc1(byte[] header, List<Entry> entries, String guid, String version, long localeId) {
        this.header = header;
        this.entries = entries;
        this.contentGuid = guid;
        this.contentVersion = version;
        this.localeId = localeId;
    }

    static Loc1 parse(byte[] data) throws IOException {
        if (data.length < 0x52 || data[0] != 'L' || data[1] != 'O' || data[2] != 'C' || data[3] != '1') {
            throw new IOException("Это не файл LOC1");
        }
        ByteBuffer b = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN);
        int schema = b.getInt(0x04);
        if (schema != 4) throw new IOException("Неподдерживаемая схема LOC1: " + schema);
        long localeId = b.getInt(0x08) & 0xFFFFFFFFL;
        int entryCount = b.getInt(0x1C);
        long headerSize = b.getLong(0x20);
        long dataOffset = b.getLong(0x28);
        long dataLength = b.getLong(0x30);

        if (headerSize + (long) entryCount * 16 != dataOffset) throw new IOException("Битый индекс LOC1");
        if (dataOffset + dataLength != data.length) throw new IOException("Битая длина данных LOC1");

        int guidLen = b.getShort(0x50) & 0xFFFF;
        String guid = new String(data, 0x52, guidLen, StandardCharsets.UTF_8);
        int verOff = 0x52 + guidLen;
        int verLen = b.getShort(verOff) & 0xFFFF;
        String version = new String(data, verOff + 2, verLen, StandardCharsets.UTF_8);

        byte[] header = new byte[(int) headerSize];
        System.arraycopy(data, 0, header, 0, (int) headerSize);

        List<Entry> entries = new ArrayList<>(entryCount);
        for (int i = 0; i < entryCount; i++) {
            int off = (int) headerSize + i * 16;
            long keyHash = b.getLong(off);
            long valueOffset = b.getInt(off + 8) & 0xFFFFFFFFL;
            long valueLength = b.getInt(off + 12) & 0xFFFFFFFFL;
            if (valueOffset == 0xFFFFFFFFL && valueLength == 0) {
                entries.add(new Entry(keyHash, true, null));
            } else {
                String value = new String(data, (int) (dataOffset + valueOffset), (int) valueLength,
                        StandardCharsets.UTF_8);
                entries.add(new Entry(keyHash, false, value));
            }
        }
        return new Loc1(header, entries, guid, version, localeId);
    }

    /** Rebuilds the container, keeping the header identity and only updating the declared length. */
    byte[] build() throws IOException {
        ByteArrayOutputStream pool = new ByteArrayOutputStream(1 << 22);
        byte[] index = new byte[entries.size() * 16];
        ByteBuffer ib = ByteBuffer.wrap(index).order(ByteOrder.LITTLE_ENDIAN);

        int cursor = 0;
        for (int i = 0; i < entries.size(); i++) {
            Entry e = entries.get(i);
            int off = i * 16;
            ib.putLong(off, e.keyHash);
            if (e.wasNull) {
                // A slot that shipped empty stays empty; filling it would change the corpus shape.
                ib.putInt(off + 8, 0xFFFFFFFF);
                ib.putInt(off + 12, 0);
                continue;
            }
            byte[] utf8 = e.value.getBytes(StandardCharsets.UTF_8);
            ib.putInt(off + 8, cursor);
            ib.putInt(off + 12, utf8.length);
            pool.write(utf8, 0, utf8.length);
            cursor += utf8.length;
        }

        byte[] poolBytes = pool.toByteArray();
        byte[] out = new byte[header.length + index.length + poolBytes.length];
        System.arraycopy(header, 0, out, 0, header.length);
        ByteBuffer ob = ByteBuffer.wrap(out).order(ByteOrder.LITTLE_ENDIAN);
        ob.putLong(0x30, poolBytes.length);
        System.arraycopy(index, 0, out, header.length, index.length);
        System.arraycopy(poolBytes, 0, out, header.length + index.length, poolBytes.length);
        return out;
    }
}
