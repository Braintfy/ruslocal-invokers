package ru.invokers.patcher;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.zip.GZIPInputStream;

/** Fetching the published translation catalog. */
final class Net {
    static final String CATALOG_URL =
            "https://raw.githubusercontent.com/Braintfy/ruslocal-invokers/main/translations/ru_RU.jsonl";

    static byte[] download(String address) throws IOException {
        HttpURLConnection connection = (HttpURLConnection) new URL(address).openConnection();
        connection.setRequestProperty("Accept-Encoding", "gzip");
        connection.setConnectTimeout(20000);
        connection.setReadTimeout(180000);
        try {
            int code = connection.getResponseCode();
            if (code != 200) throw new IOException("Сервер ответил " + code);
            InputStream stream = connection.getInputStream();
            if ("gzip".equalsIgnoreCase(connection.getContentEncoding())) {
                stream = new GZIPInputStream(stream);
            }
            ByteArrayOutputStream out = new ByteArrayOutputStream(1 << 22);
            byte[] buffer = new byte[1 << 16];
            int n;
            while ((n = stream.read(buffer)) > 0) out.write(buffer, 0, n);
            return out.toByteArray();
        } finally {
            connection.disconnect();
        }
    }
}
