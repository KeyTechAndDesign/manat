package com.keytd.manat;

import org.junit.jupiter.api.Test;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import static org.junit.jupiter.api.Assertions.assertEquals;

/** The library is advertised as dependency-free: every dependency in pom.xml must be test-scoped. */
class NoDependenciesTest {
    private static final Pattern DEPENDENCY = Pattern.compile("<dependency>(.*?)</dependency>", Pattern.DOTALL);

    @Test
    void hasNoRuntimeDependencies() throws IOException {
        String pom = new String(Files.readAllBytes(Paths.get("pom.xml")), StandardCharsets.UTF_8);
        String dependencies = pom.replaceAll("(?s)<build>.*</build>", "").replaceAll("(?s)<profiles>.*</profiles>", "");
        List<String> runtime = new ArrayList<>();
        Matcher match = DEPENDENCY.matcher(dependencies);
        while (match.find()) {
            if (!match.group(1).contains("<scope>test</scope>")) {
                runtime.add(match.group(1).replaceAll("\\s+", " ").trim());
            }
        }
        assertEquals(new ArrayList<String>(), runtime);
    }
}
