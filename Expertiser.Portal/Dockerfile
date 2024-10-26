# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Expertiser.Portal/Expertiser.Portal.csproj", "Expertiser.Portal/"]
COPY ["Expertiser.Manager/Expertiser.Manager.csproj", "Expertiser.Manager/"]
COPY ["Expertiser.Database/Expertiser.Database.csproj", "Expertiser.Database/"]
COPY ["Expertiser.Base/Expertiser.Base.csproj", "Expertiser.Base/"]
COPY ["Expertiser.Interfaces/Expertiser.Interfaces.csproj", "Expertiser.Interfaces/"]
COPY ["Rystem.Interfaces/Rystem.Interfaces.csproj", "Rystem.Interfaces/"]
COPY ["Rystem.Web/Rystem.Web.csproj", "Rystem.Web/"]
COPY ["Rystem/Rystem.csproj", "Rystem/"]
RUN dotnet restore "./Expertiser.Portal/Expertiser.Portal.csproj"
COPY . .
WORKDIR "/src/Expertiser.Portal"
RUN dotnet build "./Expertiser.Portal.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Expertiser.Portal.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Expertiser.Portal.dll"]