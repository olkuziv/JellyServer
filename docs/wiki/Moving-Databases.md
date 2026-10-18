# Moving databases

`dbtool` moves module schemas between PostgreSQL databases: **merge** several small databases into one, or **split** a module out to a database of its own. It reads the same connection strings as the server, works against any PostgreSQL host, and does not need `pg_dump`.

Typical reasons: a hosting plan limits the number of databases (merge), or one module outgrew a database (split, see [Deployment](Deployment#which-modules-to-move-first)).

## How it works

1. The target gets the module's **own migrations**, so the schema is exactly what the server expects. This works across PostgreSQL versions.
2. Tables are streamed with binary `COPY`, parents before children (foreign keys), and serial counters are realigned.
3. Row counts of every table are compared **inside the target transaction**. A mismatch rolls everything back.
4. The source is only read. It loses the schema only with `--drop-source`, after a verified copy.
5. `schema_migrations` is copied too, so the server does not run the migrations again.

Safety checks, all on by default:

| Check | Behaviour |
|---|---|
| Server still running | Refuses while a `jellyserver-*` connection is open on the source (`--ignore-running` copies a consistent snapshot, later writes are lost) |
| Target not empty | Refuses a schema that already has rows (`--replace` drops it on the target first) |
| Migration history differs | Refuses when the source is behind or ahead of the tool's version |
| Same database | Refuses to move a schema onto itself |
| Confirmation | Prints the plan first and asks, unless `--yes`. `--dry-run` only prints |
| Secrets | Passwords are never printed |

**Stop the server before moving data.**

## Getting the tool

- **In the server image:** `docker exec -it jellyserver dotnet dbtool.dll status --env`. The container already has your `ConnectionStrings__*` settings.
- **Single file** for Windows, Linux or macOS (no .NET needed):

  ```bash
  dotnet publish tools/JellyServer.DbTool -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
  ```

  Use `win-x64`, `osx-arm64`, `linux-arm64`, … as the runtime id.

## Commands

```
dbtool modules                          list the modules that own a schema
dbtool status [--env] [--db REF ...]    where each module's schema lives: size, tables, migrations, running server
dbtool merge  --from MODULE[=REF] ... --to REF
dbtool split  [--from REF] --to MODULE=REF ...
```

### Connections (`REF`)

| Form | Example |
|---|---|
| Npgsql connection string | `"Host=db1;Database=jelly;Username=u;Password=p"` |
| URI | `postgresql://u:p@db1:5432/jelly?sslmode=require` |
| Environment variable | `env:CORE_DB` (the variable holds the connection, so the password stays out of shell history) |
| File | `file:/run/secrets/core_db` |
| Name from the config file | `core` |

A `MODULE` without `=REF` is taken from the server's own settings in the environment: `ConnectionStrings__<module>`, else `ConnectionStrings__Postgres`. `split` without `--from` does the same. Inside the container this means you only name the modules and the target.

## Examples

Gather four small databases into one:

```bash
dbtool merge --from identity --from userdata --from tasks --from settings --to env:CORE_DB
```

Move the catalog out of a shared database, to its own server:

```bash
dbtool split --from env:MAIN_DB --to catalog="Host=pg-catalog;Database=catalog;Username=jelly;Password=…"
```

See the plan first:

```bash
dbtool merge --from media=env:MEDIA_DB --from people=env:PEOPLE_DB --to env:CORE_DB --dry-run
```

When it is done, the tool prints the settings the server needs:

```
ConnectionStrings__identity=Host=core-db;Database=core;Username=jelly;Password=***
```

Put the real password in, or delete a module's own `ConnectionStrings__<module>` when the target is your `ConnectionStrings__Postgres` (modules without their own string share that one). Then start the server.

## Databases behind a private network

Many hosts expose the database only inside their network, so you cannot connect from your computer. Reach it through an SSH jump host, with any of the host-specific details in the config, not in the tool:

```bash
dbtool merge --from identity=env:A --from userdata=env:B --to env:C \
  --ssh me@bastion.example.com --ssh-key ~/.ssh/id_ed25519
```

The system `ssh` client is used, so your keys, agent and `known_hosts` apply. Connections are tunnelled to the host name in each connection string (for example `db.internal`).

For several hosts, use a config file:

```json
{
  "connections": {
    "core":  { "connection": "env:CORE_DB",  "ssh": { "host": "bastion.example.com", "user": "me", "key": "~/.ssh/id_ed25519" } },
    "media": { "connection": "env:MEDIA_DB" }
  }
}
```

```bash
dbtool split --config dbtool.json --from core --to media=media
```

If you run the tool somewhere that can already reach the databases (the server container itself, a VM next to them), no SSH is needed.

## Limits

- The source must be at the same migration level as the tool. A source from an older server version has to be started once with the new server so it migrates, then moved.
- The tool does not copy roles, extensions or other schemas. If a module's migrations need an extension (the catalog uses `pg_trgm`), the target must allow `CREATE EXTENSION`.
- It moves whole module schemas. Splitting one module over two databases is not supported by design (see [Architecture](Architecture)).
