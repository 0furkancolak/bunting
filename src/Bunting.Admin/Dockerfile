# Build context is the repository root:  docker build -f src/Bunting.Admin/Dockerfile -t bunting-admin .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY build ./build
COPY src ./src
RUN dotnet publish src/Bunting.Admin/Bunting.Admin.csproj -c Release -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# The sign-in key ring lives on a volume that the non-root user can write to.
RUN mkdir /keys && chown $APP_UID /keys
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=5 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health HTTP/1.0\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q 200"]
ENTRYPOINT ["dotnet", "Bunting.Admin.dll"]
