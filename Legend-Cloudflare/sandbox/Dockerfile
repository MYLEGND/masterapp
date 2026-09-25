# Build only from this directory; never send a repository/secret context.
FROM --platform=linux/amd64 gcc:15.2.0@sha256:c101370f78e4a30be178c11dd18aeee64c65d617908a98157db2392ca73ab04f AS compiler
WORKDIR /build
COPY launcher.c probe.c ./
RUN gcc -std=c11 -O2 -Wall -Wextra -Werror -static -fstack-protector-strong -o legend-offline launcher.c \
 && gcc -std=c11 -O2 -Wall -Wextra -Werror -static -DLEGEND_TEST_UNVERIFIED_CLEANUP -o legend-offline-cleanup-fault launcher.c \
 && gcc -std=c11 -O2 -Wall -Wextra -Werror -static -o legend-probe probe.c
RUN curl --fail --silent --show-error --location https://nodejs.org/dist/v24.21.0/node-v24.21.0-linux-x64.tar.xz -o node.tar.xz \
 && echo 'fd8e59d5a511510f6a298afb548f18c7d2b1be404d8b4a27d94fbe49f56cb2d6  node.tar.xz' | sha256sum --check --strict \
 && mkdir /node && tar -xJf node.tar.xz -C /node --strip-components=1

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:60a2b2230a0d052bc54c0d453e97e331219ed503c5411470ee226859420e693c AS runtime
COPY --from=compiler /build/legend-offline /usr/local/bin/legend-offline
COPY --from=compiler /node /opt/node
RUN mkdir -p /workspace/home /workspace/tmp /opt/nuget/packages \
 && chown -R 1654:1654 /workspace \
 && chmod 0700 /workspace /workspace/home /workspace/tmp
USER 1654:1654
WORKDIR /workspace
# Platform exec invokes the launcher; no HTTP server, listener or credentials.
ENTRYPOINT ["/usr/local/bin/legend-offline", "--idle"]

FROM runtime AS test
COPY --from=compiler /build/legend-probe /usr/local/bin/legend-probe
COPY --from=compiler /build/legend-offline-cleanup-fault /usr/local/bin/legend-offline-cleanup-fault
COPY test.mjs /opt/legend/test.mjs
ENTRYPOINT ["/opt/node/bin/node", "/opt/legend/test.mjs"]

# Default builds must never launch the unfiltered test driver.
FROM runtime AS production
