## Development

If you'd like to contribute, please run:

```bash
dotnet build Source/Relayway.slnx
dotnet test --project Source/Relayway.Tests
RELAYWAY_STRESS=1 dotnet test --project Source/Relayway.Tests -- --filter-trait "Category=Stress"
```

Beware that the Docker tests skip without a Docker daemon. Live tests will also skip unless `RELAYWAY_LIVE_TENANT_ID`, `_CLIENT_ID`, `_CLIENT_SECRET`, `_SEND_FROM` and `_RECIPIENT` are set (`_CLOUD` optional).