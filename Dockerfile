# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the entire solution structure
COPY . .

# Restore using the correct relative path to the csproj
RUN dotnet restore "src/SusuCircle.Api/SusuCircle.Api.csproj"

# Build and publish using the correct path
RUN dotnet publish "src/SusuCircle.Api/SusuCircle.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS="http://+:80"
EXPOSE 80
ENTRYPOINT ["dotnet", "SusuCircle.Api.dll"]