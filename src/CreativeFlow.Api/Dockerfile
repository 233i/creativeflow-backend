# 构建上下文为仓库根目录（需要 Directory.Build.props、Directory.Packages.props 和三个项目引用），例如：
#   构建命令：podman build -f src/CreativeFlow.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23 AS build
ARG TARGETARCH

RUN case "$TARGETARCH" in \
      amd64) echo linux-musl-x64 > /rid ;; \
      arm64) echo linux-musl-arm64 > /rid ;; \
      *) echo "unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac

WORKDIR /src
# 每次发布使用隔离的还原目录，避免宿主/旧镜像缓存中的同版本包污染锁文件校验。
ENV NUGET_PACKAGES=/tmp/creativeflow-nuget-packages \
    NUGET_HTTP_CACHE_PATH=/tmp/creativeflow-nuget-http-cache
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/CreativeFlow.Domain/CreativeFlow.Domain.csproj src/CreativeFlow.Domain/
COPY src/CreativeFlow.Application/CreativeFlow.Application.csproj src/CreativeFlow.Application/
COPY src/CreativeFlow.Infrastructure/CreativeFlow.Infrastructure.csproj src/CreativeFlow.Infrastructure/
COPY src/CreativeFlow.Api/CreativeFlow.Api.csproj src/CreativeFlow.Api/
COPY src/CreativeFlow.Api/packages.lock.json src/CreativeFlow.Api/
COPY src/CreativeFlow.Infrastructure/packages.lock.json src/CreativeFlow.Infrastructure/
# 先校验已验收的依赖锁，再还原目标平台运行时包；发布不重新解析依赖。
RUN dotnet restore src/CreativeFlow.Api/CreativeFlow.Api.csproj --locked-mode && \
    dotnet restore src/CreativeFlow.Api/CreativeFlow.Api.csproj -r "$(cat /rid)"

COPY src/CreativeFlow.Domain/ src/CreativeFlow.Domain/
COPY src/CreativeFlow.Application/ src/CreativeFlow.Application/
COPY src/CreativeFlow.Infrastructure/ src/CreativeFlow.Infrastructure/
COPY db/migrations/ db/migrations/
COPY src/CreativeFlow.Api/ src/CreativeFlow.Api/
RUN dotnet publish src/CreativeFlow.Api/CreativeFlow.Api.csproj -c Release -r "$(cat /rid)" \
      --self-contained true /p:PublishSingleFile=true --no-restore -o /app/publish

# JIT 自包含单文件不需要目标主机安装 .NET 运行时。InvariantGlobalization
# （Directory.Build.props）移除了 ICU 依赖；显式保留运行时使用的 OpenSSL、Brotli 与 ffmpeg。
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.10-alpine3.23 AS runtime
# 网络受限时可选择受信任的 Alpine 镜像；保持 APK 签名校验开启。
ARG ALPINE_MIRROR=https://dl-cdn.alpinelinux.org/alpine
RUN case "$ALPINE_MIRROR" in https://*) ;; *) echo "ALPINE_MIRROR must use HTTPS" >&2; exit 1 ;; esac && \
    sed -i "s#https://dl-cdn.alpinelinux.org/alpine#${ALPINE_MIRROR%/}#g" /etc/apk/repositories && \
    apk add --no-cache libssl3 brotli-libs ffmpeg
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["./CreativeFlow.Api"]
