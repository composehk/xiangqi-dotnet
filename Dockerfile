FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Xiangqi.Core/Xiangqi.Core.csproj src/Xiangqi.Core/
COPY src/Xiangqi.Engine/Xiangqi.Engine.csproj src/Xiangqi.Engine/
COPY src/Xiangqi.Server/Xiangqi.Server.csproj src/Xiangqi.Server/
RUN dotnet restore src/Xiangqi.Server/Xiangqi.Server.csproj
COPY src/ src/
ARG GIT_SHA=unknown
RUN dotnet publish src/Xiangqi.Server/Xiangqi.Server.csproj -c Release -o /app/publish --no-restore -p:SourceRevisionId=$GIT_SHA

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "Xiangqi.Server.dll"]
