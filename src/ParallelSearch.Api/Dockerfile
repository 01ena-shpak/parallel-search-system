FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY src/ParallelSearch.Api/ParallelSearch.Api.csproj src/ParallelSearch.Api/
COPY src/ParallelSearch.Core/ParallelSearch.Core.csproj src/ParallelSearch.Core/
RUN dotnet restore src/ParallelSearch.Api/ParallelSearch.Api.csproj

COPY src/ParallelSearch.Api/ src/ParallelSearch.Api/
COPY src/ParallelSearch.Core/ src/ParallelSearch.Core/
RUN dotnet publish src/ParallelSearch.Api/ParallelSearch.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ParallelSearch.Api.dll"]
