# Stage 1: Build React 19 + Fluent UI v9 operator SPA
FROM node:22-alpine AS web-build
WORKDIR /web
COPY web/package.json web/package-lock.json* ./
RUN npm install --legacy-peer-deps
COPY web/ ./
RUN npm run build

# Stage 2: Build .NET 11 Demo-Factory API + Orchestrator (Lean single-container or Reference API)
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS dotnet-build
WORKDIR /src
COPY Directory.Build.props DemoFactory.slnx ./
COPY src/ ./src/
RUN dotnet publish src/DemoFactory.Api/DemoFactory.Api.csproj -c Release -o /app/publish

# Stage 3: Final minimal runtime image
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
COPY --from=dotnet-build /app/publish ./
COPY --from=web-build /web/dist ./wwwroot
COPY agents/ ./agents/
COPY policies/ ./policies/
COPY templates/ ./templates/
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DemoFactory.Api.dll"]
