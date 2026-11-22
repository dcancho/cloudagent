FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && for existing in $(getent passwd 1000 1001 | cut -d: -f1); do userdel "$existing"; done \
    && useradd --uid 1000 --no-create-home --shell /usr/sbin/nologin runner \
    && useradd --uid 1001 --no-create-home --shell /usr/sbin/nologin coder \
    && git config --system safe.directory '*'

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_gcServer=0

USER 1000:1000
WORKDIR /work
