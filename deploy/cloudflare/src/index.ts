// The public Workbench demo on Cloudflare Containers (docs/demo-hosting.md). This Worker is the only way in: it sends
// every request to one container running the repository's `demo` image, starting it on the first request.
import { Container, getContainer } from "@cloudflare/containers";

interface Env {
  WORKBENCH: DurableObjectNamespace<WorkbenchDemo>;
}

export class WorkbenchDemo extends Container<Env> {
  defaultPort = 8080;
  // The container's disk is not kept when it stops, so a sleeping demo comes back with fresh sample data. Keep it up
  // through a working day of testing.
  sleepAfter = "12h";
  envVars = {
    // Only this Worker can reach the container, so trust the forwarding headers it sets.
    ForwardedHeaders__KnownNetworks__0: "0.0.0.0/0",
    ForwardedHeaders__KnownNetworks__1: "::/0",
  };

  override async fetch(request: Request): Promise<Response> {
    // A cold start migrates and seeds the database before the port opens, which takes longer than the default wait.
    if (!this.ctx.container?.running) {
      await this.startAndWaitForPorts(this.defaultPort, { portReadyTimeoutMS: 180_000 });
    }
    return this.containerFetch(request, this.defaultPort);
  }
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const headers = new Headers(request.headers);
    headers.set("X-Forwarded-Proto", url.protocol.replace(":", ""));
    headers.set("X-Forwarded-Host", url.host);
    headers.set("X-Forwarded-For", request.headers.get("CF-Connecting-IP") ?? "");
    return getContainer(env.WORKBENCH).fetch(new Request(request, { headers }));
  },
};
