FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src
ARG TARGETARCH
ARG PROJECT_FILE
COPY . .
RUN arch_args="" && \
    if [ -n "$TARGETARCH" ]; then \
        if [ "$TARGETARCH" = "amd64" ]; then arch_args="-a x64"; \
        elif [ "$TARGETARCH" = "arm64" ]; then arch_args="-a arm64"; \
        else arch_args="-a $TARGETARCH"; fi; \
    fi && \
    dotnet publish "$PROJECT_FILE" $arch_args --configuration Release --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.source=https://github.com/SwiftDrop-Group09/swiftdrop-backend
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl fontconfig libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /tmp/swiftdrop/identity /home/app/.aspnet/DataProtection-Keys \
    && chown -R "$APP_UID:$APP_UID" /tmp/swiftdrop /home/app/.aspnet \
    && chmod 700 /tmp/swiftdrop/identity /home/app/.aspnet/DataProtection-Keys
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --retries=5 \
    CMD curl --fail --silent http://127.0.0.1:8080/health || exit 1
USER $APP_UID
ENTRYPOINT ["dotnet"]
