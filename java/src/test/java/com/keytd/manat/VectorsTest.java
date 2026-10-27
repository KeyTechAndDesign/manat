package com.keytd.manat;

import com.keytd.manat.internal.Json;
import com.keytd.manat.internal.Values;
import com.keytd.manat.providers.Azericard;
import com.keytd.manat.providers.Epoint;
import com.keytd.manat.providers.GoldenPay;
import com.keytd.manat.providers.KapitalBank;
import com.keytd.manat.providers.Payriff;
import com.keytd.manat.providers.Yigim;
import org.junit.jupiter.api.DynamicTest;
import org.junit.jupiter.api.TestFactory;
import org.opentest4j.AssertionFailedError;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.time.Clock;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.stream.Collectors;
import java.util.stream.Stream;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;

/** Runs every vector in spec/vectors, the same files the other languages run. */
class VectorsTest {
    private static final Path VECTORS = Paths.get("..", "spec", "vectors");

    @TestFactory
    Stream<DynamicTest> vectors() throws IOException {
        List<Path> files;
        try (Stream<Path> walk = Files.walk(VECTORS, 2)) {
            files = walk.filter(path -> path.toString().endsWith(".json")).sorted().collect(Collectors.toList());
        }
        assertFalse(files.isEmpty(), "no vectors found in " + VECTORS.toAbsolutePath());
        return files.stream().map(file -> {
            String name = file.getParent().getFileName() + "/" + file.getFileName();
            return DynamicTest.dynamicTest(name, () -> run(Values.map(Json.parse(read(file)))));
        });
    }

    private static void run(Map<String, Object> vector) {
        String provider = (String) vector.get("provider");
        String operation = (String) vector.get("operation");
        Map<String, Object> config = Values.map(vector.get("config"));

        if (operation.equals("mapStatus")) {
            for (Object testCase : Values.list(vector.get("cases"))) {
                Object input = Values.get(testCase, "input");
                assertEquals(Values.get(testCase, "expected", "status"), mapStatus(provider, Values.map(input)).value(), Json.stringify(input));
            }
            return;
        }
        if (operation.equals("macSource")) {
            for (Object testCase : Values.list(vector.get("cases"))) {
                Map<String, String> fields = new LinkedHashMap<>();
                Values.map(Values.get(testCase, "input", "fields")).forEach((key, value) -> fields.put(key, (String) value));
                List<String> order = Values.list(Values.get(testCase, "input", "order")).stream().map(String.class::cast).collect(Collectors.toList());
                assertEquals(Values.get(testCase, "expected", "source"), Azericard.macSource(fields, order));
            }
            return;
        }
        if (operation.equals("sign")) {
            for (Object testCase : Values.list(vector.get("cases"))) {
                Expect.equal(Values.get(testCase, "expected"), Epoint.encode((String) config.get("privateKey"), (String) Values.get(testCase, "input", "json")), "sign");
            }
            return;
        }

        ScriptedTransport transport = new ScriptedTransport(Values.list(vector.get("http")));
        Map<String, Object> input = Values.map(vector.get("input"));
        Map<String, Object> expected = vector.get("expected") == null ? new LinkedHashMap<>() : Values.map(vector.get("expected"));
        if (expected.containsKey("error")) {
            ManatError error = assertThrows(ManatError.class, () -> {
                try {
                    invoke(new Manat(provider(provider, config, transport)), operation, input);
                } catch (ManatError wrapped) {
                    // A failed request check inside the transport would come back as a network_error.
                    if (wrapped.getCause() instanceof AssertionFailedError) {
                        throw (AssertionFailedError) wrapped.getCause();
                    }
                    throw wrapped;
                }
            });
            assertEquals(expected.get("error"), error.code().value(), error.getMessage());
            if (expected.get("providerCode") != null) {
                assertEquals(expected.get("providerCode"), error.providerCode());
            }
        } else {
            Expect.subset(expected, invoke(new Manat(provider(provider, config, transport)), operation, input), "result");
        }
        transport.assertDone();
    }

    /** Calls the operation with the vector's JSON input turned into the typed request, and returns the result's map. */
    private static Map<String, Object> invoke(Manat manat, String operation, Map<String, Object> input) {
        switch (operation) {
            case "createPayment":
                return manat.createPayment(createRequest(input)).toMap();
            case "verifyCallback":
                return manat.verifyCallback(callback(input)).toMap();
            case "getStatus":
                return manat.getStatus((String) input.get("paymentId")).toMap();
            case "refund": {
                RefundRequest request = RefundRequest.of((String) input.get("paymentId")).withProviderRef((String) input.get("providerRef"));
                return manat.refund(input.get("amount") == null ? request : request.withAmount(money(input.get("amount")))).toMap();
            }
            case "capture":
                return manat.capture(action(input)).toMap();
            case "cancel":
                return manat.cancel(action(input)).toMap();
            default:
                throw new IllegalArgumentException("unknown operation " + operation);
        }
    }

    private static CreatePaymentRequest createRequest(Map<String, Object> input) {
        CreatePaymentRequest request = CreatePaymentRequest.of((String) input.get("orderId"), money(input.get("amount")), (String) input.get("returnUrl"));
        if (input.get("callbackUrl") != null) {
            request = request.withCallbackUrl((String) input.get("callbackUrl"));
        }
        if (input.get("description") != null) {
            request = request.withDescription((String) input.get("description"));
        }
        if (input.get("language") != null) {
            request = request.withLanguage(Language.valueOf(((String) input.get("language")).toUpperCase(Locale.ROOT)));
        }
        if ("authorize".equals(input.get("intent"))) {
            request = request.withIntent(Intent.AUTHORIZE);
        }
        if (input.get("customer") != null) {
            request = request.withCustomer((String) Values.get(input, "customer", "email"), (String) Values.get(input, "customer", "phone"));
        }
        if (input.get("metadata") != null) {
            Map<String, String> metadata = new LinkedHashMap<>();
            Values.map(input.get("metadata")).forEach((key, value) -> metadata.put(key, (String) value));
            request = request.withMetadata(metadata);
        }
        return request;
    }

    private static PaymentAction action(Map<String, Object> input) {
        PaymentAction action = PaymentAction.of((String) input.get("paymentId")).withProviderRef((String) input.get("providerRef"));
        return input.get("amount") == null ? action : action.withAmount(money(input.get("amount")));
    }

    private static IncomingCallback callback(Map<String, Object> input) {
        IncomingCallback callback = IncomingCallback.of((String) input.get("method"));
        if (input.get("query") != null) {
            callback = callback.withQuery(Values.map(input.get("query")));
        }
        if (input.get("body") instanceof String) {
            callback = callback.withBody((String) input.get("body"));
        } else if (input.get("body") != null) {
            callback = callback.withBody(Values.map(input.get("body")));
        }
        if (input.get("headers") != null) {
            callback = callback.withHeaders(Values.map(input.get("headers")));
        }
        return callback;
    }

    /**
     * {@code {amount, currency}} → Money. Money holds a long, so a fractional amount (the float vectors) cannot reach the
     * library at all; the type system is the check, and it is reported the way the other languages report it.
     */
    private static Money money(Object value) {
        Map<String, Object> map = Values.map(value);
        Long amount = map == null ? null : Values.integer(map.get("amount"));
        if (amount == null || !(map.get("amount") instanceof Long)) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "amount must be an integer in minor units, got " + value);
        }
        Currency currency = Currency.find((String) map.get("currency"));
        if (currency == null) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "currency " + map.get("currency") + " is not supported");
        }
        return Money.of(amount, currency);
    }

    private static PaymentStatus mapStatus(String provider, Map<String, Object> input) {
        switch (provider) {
            case "kapitalbank":
                return KapitalBank.mapStatus(input);
            case "payriff":
                return Payriff.mapStatus((String) input.get("paymentStatus"));
            case "epoint":
                return Epoint.mapStatus((String) input.get("status"));
            case "goldenpay":
                return GoldenPay.mapStatus(Values.integer(Values.get(input, "status", "code")));
            case "yigim":
                return Yigim.mapStatus((String) input.get("status"));
            default:
                throw new IllegalArgumentException(provider + " has no mapStatus");
        }
    }

    /**
     * Builds the adapter from the JSON-friendly vector config, the same way in every language: {@code <name>File} is read
     * relative to the provider's vector folder, {@code clock} (ISO time) and {@code nonce} (string) become fixed generators.
     */
    private static PaymentProvider provider(String name, Map<String, Object> raw, ScriptedTransport transport) {
        Map<String, Object> config = new LinkedHashMap<>();
        raw.forEach((key, value) -> {
            if (key.endsWith("File") && value instanceof String) {
                config.put(key.substring(0, key.length() - 4), read(VECTORS.resolve(name).resolve((String) value)));
            } else {
                config.put(key, value);
            }
        });
        Environment environment = config.get("environment") == null ? null : Environment.valueOf(((String) config.get("environment")).toUpperCase(Locale.ROOT));
        switch (name) {
            case "kapitalbank": {
                KapitalBank.Builder builder = KapitalBank.builder().transport(transport)
                        .username((String) config.get("username")).password((String) config.get("password")).baseUrl((String) config.get("baseUrl"));
                return (environment == null ? builder : builder.environment(environment)).build();
            }
            case "payriff":
                return Payriff.builder().transport(transport).secretKey((String) config.get("secretKey")).baseUrl((String) config.get("baseUrl")).build();
            case "epoint":
                return Epoint.builder().transport(transport)
                        .publicKey((String) config.get("publicKey")).privateKey((String) config.get("privateKey")).baseUrl((String) config.get("baseUrl")).build();
            case "goldenpay": {
                GoldenPay.Builder builder = GoldenPay.builder().transport(transport)
                        .authKey((String) config.get("authKey")).merchantName((String) config.get("merchantName")).baseUrl((String) config.get("baseUrl"));
                return (environment == null ? builder : builder.environment(environment)).build();
            }
            case "yigim": {
                Yigim.Builder builder = Yigim.builder().transport(transport)
                        .merchant((String) config.get("merchant")).apiKey((String) config.get("apiKey")).biller((String) config.get("biller"))
                        .template((String) config.get("template")).baseUrl((String) config.get("baseUrl"));
                return (environment == null ? builder : builder.environment(environment)).build();
            }
            case "azericard": {
                Azericard.Builder builder = Azericard.builder().transport(transport)
                        .terminal((String) config.get("terminal"))
                        .privateKey((String) config.get("privateKey"))
                        .gatewayPublicKey((String) config.get("gatewayPublicKey"))
                        .merchantName((String) config.get("merchantName"))
                        .merchantUrl((String) config.get("merchantUrl"))
                        .gatewayUrl((String) config.get("gatewayUrl"))
                        .email((String) config.get("email"))
                        .country((String) config.get("country"))
                        .merchantGmt((String) config.get("merchantGmt"));
                if (environment != null) {
                    builder.environment(environment);
                }
                if (config.get("clock") != null) {
                    builder.clock(Clock.fixed(Instant.parse((String) config.get("clock")), ZoneOffset.UTC));
                }
                if (config.get("nonce") != null) {
                    String nonce = (String) config.get("nonce");
                    builder.nonce(() -> nonce);
                }
                if (config.get("macFields") != null) {
                    Map<String, List<String>> macFields = new LinkedHashMap<>();
                    Values.map(config.get("macFields")).forEach((key, value) -> {
                        List<String> fields = new ArrayList<>();
                        Values.list(value).forEach(field -> fields.add((String) field));
                        macFields.put(key, fields);
                    });
                    builder.macFields(macFields);
                }
                return builder.build();
            }
            default:
                throw new IllegalArgumentException("no adapter registered for " + name);
        }
    }

    private static String read(Path file) {
        try {
            return new String(Files.readAllBytes(file), StandardCharsets.UTF_8);
        } catch (IOException error) {
            throw new UncheckedIOException(error);
        }
    }
}
