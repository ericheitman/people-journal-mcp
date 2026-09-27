FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PeopleJournal.Mcp/PeopleJournal.Mcp.csproj src/PeopleJournal.Mcp/
RUN dotnet restore src/PeopleJournal.Mcp/PeopleJournal.Mcp.csproj
COPY src/ src/
RUN dotnet publish src/PeopleJournal.Mcp/PeopleJournal.Mcp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5191 \
    Journal__RootPath=/data/journal
EXPOSE 5191
USER $APP_UID
ENTRYPOINT ["dotnet", "PeopleJournal.Mcp.dll"]
