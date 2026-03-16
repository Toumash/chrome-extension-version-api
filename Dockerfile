FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
ARG TARGETARCH
RUN apt-get update && apt-get install -y clang zlib1g-dev
WORKDIR /src
COPY ChromeExtensionVersionApi.csproj .
RUN dotnet restore -a $TARGETARCH
COPY . .
RUN dotnet publish -c Release -a $TARGETARCH -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-preview-noble-chiseled
WORKDIR /app
COPY --from=build /app/ChromeExtensionVersionApi .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["./ChromeExtensionVersionApi"]
