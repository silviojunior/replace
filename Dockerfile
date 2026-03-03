FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["RePlace.csproj", "./"]
RUN dotnet restore "RePlace.csproj"
COPY . .
RUN dotnet build "RePlace.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "RePlace.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

COPY --from=publish /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

USER app

ENTRYPOINT ["dotnet", "RePlace.dll"]
