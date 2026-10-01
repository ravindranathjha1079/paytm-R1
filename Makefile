BASE_URL ?= http://localhost:8080

.PHONY: up down test burst logs

up:
	docker compose up -d --build

down:
	docker compose down

test:
	dotnet test SeatRes.slnx

burst:
	./burst.sh $(BASE_URL)

logs:
	docker compose logs -f api
