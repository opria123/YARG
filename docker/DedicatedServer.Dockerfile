FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    libgl1 \
    libxcursor1 \
    libxrandr2 \
    libxi6 \
    libxinerama1 \
    && rm -rf /var/lib/apt/lists/*

# Enable dedicated server mode
ENV YARG_DEDICATED=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

WORKDIR /app
COPY Build/DedicatedServer/ /app/

# Create default config directory and copy example config if available
RUN mkdir -p /app/config

RUN useradd --no-log-init --system --home /app yarg \
    && chown -R yarg /app \
    && chmod +x /app/YARGServer
USER yarg

# Game port (UDP/TCP) and Admin web UI port
EXPOSE 9050/udp 9050/tcp 8080/tcp

# Mount point for persistent data including config file
VOLUME ["/app/config"]

ENTRYPOINT ["./YARGServer"]
CMD ["-batchmode", "-nographics", "-dedicated", "-persistent-data-path", "/app/config"]
