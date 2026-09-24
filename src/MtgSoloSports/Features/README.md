# Features

Backend application code is organized by vertical slice/use case.

A feature normally owns its endpoint, request/response contract, handler, feature-specific persistence/query logic and tests. Do not introduce horizontal Application/Domain/Infrastructure layers or MediatR.
