# Combined Comfy and Lumberjacks P7 environment

This Terraform root migrates the memory-starved `am4` dedicated-server role to a
64 GiB native-Linux Compute Engine VM. It runs the real combined product:

- the Steam-only Valheim dedicated server and `ComfyEra16` state;
- the ComfyNetworkSense replacement mod already present in the migrated state;
- Lumberjacks Gateway, EventLog, Progression, Operator API, and PostgreSQL; and
- host/container/OTLP telemetry through the Google Cloud Ops Agent.

OMEN remains the rendered Valheim client and fieldlab controller. The Comfy MCP
gateway remains a development observability surface on OMEN and reaches this VM by
SSH; it is not placed in the gameplay path.

The checked-in Compose file pins the exact Valheim image digest observed on `am4`.
The migration procedure must also preserve these source invariants:

| Artifact | Source SHA-256 |
|---|---|
| `ComfyNetworkSense.dll` 0.5.18 | `827fc6b2c3f2781039be9e9cb31c3db839a3e93ac38a418527cf17fcdc4f816d` |
| root BepInEx configuration | `065e942174d0912ca94d108794b4d59bbdec34e2e21a299a31b63efc6a017d01` |
| `ComfyEra16.db` baseline | `4513d0348e9f740cad22032c476c5dd6f5304490dc05912f35b250837e25d49a` |
| `ComfyEra16.fwl` baseline | `5f323fbe7b627fd50520d8f4f6dedd13027a92bfe056013aa52d7306d09a3539` |

The world hashes are migration baselines, not permanent expected values: after a
clean save during cutover, record a new manifest and require the source and target
archives to match that manifest byte-for-byte.

Copy `terraform.tfvars.example` to ignored `terraform.tfvars`, replace the project,
operator email, and OMEN CIDR, then run `terraform init`, `terraform plan`, and
`terraform apply`. Secrets never belong in Terraform state. Create
`/etc/comfy-p7/environment` on the VM with mode `0600`:

```text
GOOGLE_CLOUD_PROJECT=<project>
LUMBERJACKS_VERSION=<commit-sha>
POSTGRES_PASSWORD=<random-stage-password>
VALHEIM_SERVER_PASSWORD=<existing-lab-password>
LUMBERJACKS_ROOT=/opt/lumberjacks
```

Do not start the GCP Valheim container until the source server is cleanly stopped and
the final state archive has been verified. Never allow `am4` and GCP to write the same
world lineage concurrently.
