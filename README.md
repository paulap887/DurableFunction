# Order Processing — Azure Durable Functions

A working example of an Azure Durable Functions order processing workflow built with .NET 9 and the isolated worker model. Covers the full orchestration pattern: HTTP trigger → orchestrator → activity functions → status polling.

## Workflow

```
POST /api/orders
       │
       ▼
 RunOrderOrchestration
       │
       ├── ValidateOrder (activity)
       ├── ProcessPayment (activity)
       └── SendConfirmationEmail (activity)
```

Each step is checkpointed. If the host crashes mid-run, the orchestration picks up exactly where it left off.

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| .NET SDK | 9.0+ | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| Azure Functions Core Tools | 4.x | `npm install -g azure-functions-core-tools@4` |
| Azurite | any | `npm install -g azurite` or `brew install azurite` |

Verify installs:
```bash
dotnet --version   # 9.0.x
func --version     # 4.x.x
azurite --version
```

## Getting Started

### 1. Clone

```bash
git clone [<repo-url>](https://github.com/paulap887/DurableFunction/)
cd DurableFunction
```

### 2. Restore packages

```bash
dotnet restore OrderProcessing.csproj
```

### 3. Start Azurite

Open a dedicated terminal and leave it running:

```bash
azurite --silent --location ./azurite
```

Durable Functions persist orchestration state to Azure Storage. Azurite emulates this locally. If Azurite isn't running when you start the function, you'll see storage connection errors in the logs.

### 4. Build and start the function

```bash
dotnet build OrderProcessing.csproj && func start --no-build --script-root bin/Debug/net9.0
```

When ready, you'll see:

```
Functions:
    CreateSampleOrder:    [GET]  http://localhost:7071/api/orders/sample
    GetOrderStatus:       [GET]  http://localhost:7071/api/orders/{instanceId}
    StartOrderProcessing: [POST] http://localhost:7071/api/orders
```

> **Why not just `func start`?** This project has both a `.sln` and a `.csproj` in the same directory. Running `func start` without flags triggers MSBuild which fails with `MSB1011: more than one project or solution file`. The explicit build + `--script-root` combination works around this.

### 5. Get your function key

The endpoints use `AuthorizationLevel.Function`. Fetch the local key from the admin API:

```bash
curl http://localhost:7071/admin/host/keys
```

Copy the `value` from the response. You'll pass it as `?code=<key>` on every request.

## Testing the Full Flow

### Submit an order

```bash
curl -X POST "http://localhost:7071/api/orders?code=<your-key>" \
  -H "Content-Type: application/json" \
  -d '{
    "customerName": "Jane Smith",
    "customerEmail": "jane@example.com",
    "items": [
      { "productId": "PROD001", "productName": "Laptop", "quantity": 1, "price": 999.99 },
      { "productId": "PROD002", "productName": "Wireless Mouse", "quantity": 2, "price": 29.99 }
    ],
    "totalAmount": 1059.97
  }'
```

Response (`202 Accepted`):
```json
{
  "orderId": "7568c73d-...",
  "instanceId": "dafb0e5d...",
  "message": "Order processing started successfully"
}
```

### Check status

```bash
curl "http://localhost:7071/api/orders/<instanceId>?code=<your-key>"
```

Poll until `runtimeStatus` changes from `Running` to `Completed` (~5 seconds — the payment activity has a 2s delay and the email activity has a 1s delay).

Final response:
```json
{
  "instanceId": "dafb0e5d...",
  "runtimeStatus": "Completed",
  "output": {
    "Success": true,
    "Message": "Order processed successfully",
    "Order": { "Status": 4, ... }
  }
}
```

### Get a sample order payload

```bash
curl "http://localhost:7071/api/orders/sample?code=<your-key>"
```

Returns a pre-filled order body ready to POST.

## Project Structure

```
DurableFunction/
├── Functions/
│   ├── OrderHttpTrigger.cs    # HTTP endpoints (submit order, check status, sample)
│   ├── OrderOrchestrator.cs   # Orchestration logic
│   └── OrderActivities.cs     # ValidateOrder, ProcessPayment, SendConfirmationEmail
├── Models/
│   └── Order.cs               # Order, OrderItem, OrderStatus, OrderResult
├── Program.cs                 # Host builder / DI setup
├── OrderProcessing.csproj
├── host.json                  # Durable Task hub config
└── local.settings.json        # Local dev settings (not committed)
```

## Configuration

**`host.json`** — task hub name and concurrency limits:
```json
{
  "extensions": {
    "durableTask": {
      "hubName": "OrderProcessingHub",
      "maxConcurrentActivityFunctions": 10,
      "maxConcurrentOrchestratorFunctions": 10
    }
  }
}
```

**`local.settings.json`** — points to Azurite for local storage (never commit this):
```json
{
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated"
  }
}
```
  
## License

MIT
