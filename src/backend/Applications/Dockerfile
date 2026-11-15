FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/backend/ src/backend/
COPY scripts/db/ scripts/db/
RUN dotnet restore src/backend/Applications/Peritial.Applications.csproj
RUN dotnet publish src/backend/Applications/Peritial.Applications.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Peritial.Applications.dll"]
