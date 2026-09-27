FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source
COPY src/ActivityTracker.Api/ActivityTracker.Api.csproj src/ActivityTracker.Api/
RUN dotnet restore src/ActivityTracker.Api/ActivityTracker.Api.csproj
COPY src/ActivityTracker.Api/ src/ActivityTracker.Api/
RUN dotnet publish src/ActivityTracker.Api/ActivityTracker.Api.csproj -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /publish .
USER root
RUN mkdir /data && chown app:app /data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ConnectionStrings__Tracker="Data Source=/data/tracker.db;Foreign Keys=True;Default Timeout=5"
EXPOSE 8080
ENTRYPOINT ["dotnet", "ActivityTracker.Api.dll"]
