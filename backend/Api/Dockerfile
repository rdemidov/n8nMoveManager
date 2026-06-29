FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY backend/Domain/Domain.csproj backend/Domain/
COPY backend/Application/Application.csproj backend/Application/
COPY backend/Infrastructure/Infrastructure.csproj backend/Infrastructure/
COPY backend/Api/Api.csproj backend/Api/
RUN dotnet restore backend/Api/Api.csproj

COPY backend/ backend/
RUN dotnet publish backend/Api/Api.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5107

COPY --from=build /app/publish ./
RUN mkdir -p /app/App_Data && chown -R "$APP_UID:$APP_UID" /app

USER $APP_UID
EXPOSE 5107
ENTRYPOINT ["dotnet", "Api.dll"]
