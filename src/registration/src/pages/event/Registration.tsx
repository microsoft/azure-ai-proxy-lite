import { useClientPrincipal } from "@aaronpowell/react-static-web-apps-auth";
import {
  Button,
  Link,
  Toast,
  ToastTitle,
  ToastTrigger,
  Toaster,
  makeStyles,
  shorthands,
  useId,
  useToastController,
} from "@fluentui/react-components";
import { CopyRegular, EyeOffRegular, EyeRegular } from "@fluentui/react-icons";
import { useEffect, useReducer } from "react";
import ReactMarkdown from "react-markdown";
import { Form, useLoaderData, useParams } from "react-router-dom";
import { reducer } from "./Registration.reducers";
import type { FoundryToolkitEndpoint, AttendeeRegistration, EventDetails, McpServerEndpoint } from "./Registration.state";

const useStyles = makeStyles({
  container: {
    display: "flex",
    flexDirection: "column",
    ...shorthands.margin("0px", "80px"),
    fontSize: "20px",
    fontFamily: "Arial, Verdana, sans-serif",
    lineHeight: "1.5",
    "@media (max-width: 768px)": {
      marginLeft: "16px",
      marginRight: "16px",
      fontSize: "16px",
    },
  },
  field: {
    display: "flex",
    flexDirection: "column",
    ...shorthands.margin("0px", "0px", "16px", "0px"),
  },
  fieldLabel: {
    fontSize: "14px",
    fontWeight: "600",
    color: "#444",
    ...shorthands.margin("0px", "0px", "4px", "0px"),
  },
  fieldRow: {
    display: "flex",
    alignItems: "center",
    columnGap: "4px",
  },
  detailsSection: {
    maxWidth: "75%",
    "@media (max-width: 768px)": {
      maxWidth: "100%",
    },
  },
  fullWidthInput: {
    flexGrow: 1,
    minWidth: "0px",
  },
  toolkitDescription: {
    ...shorthands.margin("4px", "0px", "12px", "0px"),
    fontSize: "16px",
    color: "#555",
  },
  toolkitCard: {
    ...shorthands.border("1px", "solid", "#e0e0e0"),
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("12px", "16px"),
    ...shorthands.margin("0px", "0px", "12px", "0px"),
    backgroundColor: "#fafafa",
    display: "grid",
    gridTemplateColumns: "auto 1fr auto",
    columnGap: "8px",
    rowGap: "8px",
    alignItems: "center",
    "@media (max-width: 768px)": {
      gridTemplateColumns: "1fr",
      "& > *": {
        minWidth: "0px",
      },
    },
  },
  toolkitLabel: {
    fontSize: "14px",
    fontWeight: "600",
    color: "#444",
    whiteSpace: "nowrap" as const,
    "@media (max-width: 768px)": {
      whiteSpace: "normal" as const,
    },
  },
  toolkitValue: {
    fontSize: "15px",
    color: "#333",
    fontWeight: "600",
  },
  toolkitEndpointValue: {
    fontSize: "14px",
    color: "#333",
    fontFamily: "monospace",
    whiteSpace: "nowrap" as const,
    overflowX: "hidden" as const,
    textOverflow: "ellipsis" as const,
    minWidth: "0px",
    "@media (max-width: 768px)": {
      whiteSpace: "normal" as const,
      overflowWrap: "break-word" as const,
      wordBreak: "break-word" as const,
      overflowX: "visible" as const,
    },
  },
  codeCardWrapper: {
    position: "relative" as const,
    ...shorthands.margin("12px", "0px", "16px", "0px"),
  },
  codeCardCopyButton: {
    position: "absolute" as const,
    top: "8px",
    right: "8px",
  },
  codeCard: {
    ...shorthands.border("1px", "solid", "#e0e0e0"),
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("16px"),
    backgroundColor: "#f8f8f8",
    overflowX: "auto" as const,
    "@media (max-width: 768px)": {
      ...shorthands.padding("8px"),
    },
  },
});

export const Registration = () => {
  const { event, attendee } = useLoaderData() as {
    event: EventDetails;
    attendee?: AttendeeRegistration;
  };
  const { id: routeEventId } = useParams<{ id: string }>();

  const styles = useStyles();

  const [state, dispatch] = useReducer(reducer, {
    profileLoaded: false,
    showApiKey: false,
  });
  const { loaded, clientPrincipal } = useClientPrincipal();

  useEffect(() => {
    dispatch({
      type: "PROFILE_LOADED",
      payload: { loaded, profile: clientPrincipal || undefined },
    });
  }, [loaded, clientPrincipal]);

  const toasterId = useId("toaster");
  const { dispatchToast } = useToastController(toasterId);

  const notify = () =>
    dispatchToast(
      <Toast>
        <ToastTitle
          action={
            <ToastTrigger>
              <Link>Dismiss</Link>
            </ToastTrigger>
          }
        >
          Copied to clipboard.
        </ToastTitle>
      </Toast>,
      { position: "top", intent: "success" }
    );

  const copyToClipboard = async (value: string) => {
    await navigator.clipboard.writeText(value);
    notify();
  };

  const toEnvVarName = (name: string) =>
    name
      .toUpperCase()
      .replace(/[^A-Z0-9]+/g, "_")
      .replace(/^_+|_+$/g, "");

  const buildLabConfig = (): string => {
    // Strip CR/LF from any value written into the .env file to prevent
    // injection of additional lines via attacker- or typo-controlled fields.
    const safe = (v: unknown) => String(v ?? "").replace(/[\r\n]+/g, " ");

    const proxyEndpoint =
      event?.proxyUrl ?? `${window.location.origin}/api/v1`;
    const eventName = (event?.eventCode ?? "event").trim();
    const expiresIso = event?.endTimestamp
      ? new Date(event.endTimestamp).toISOString()
      : "unknown";
    const lines: string[] = [];
    lines.push(`# ${safe(eventName)}`);
    lines.push(`# Event config generated ${safe(new Date().toISOString())}`);
    lines.push(`# API key expires ${safe(expiresIso)}`);
    lines.push("");
    lines.push(`EVENT_API_KEY=${safe(attendee?.apiKey)}`);
    lines.push("");

    // Exclude any models exposed via the Foundry Toolkit.
    const foundryToolkitNames = new Set(
      (event?.foundryToolkitEndpoints ?? []).map(
        (ep: FoundryToolkitEndpoint) => ep.deploymentName
      )
    );
    const modelNames = Object.entries(event?.capabilities ?? {})
      .sort(([a], [b]) => a.localeCompare(b))
      .flatMap(([, names]) =>
        [...names]
          .filter((n) => !foundryToolkitNames.has(n))
          .sort((a, b) => a.localeCompare(b))
      );

    if (modelNames.length > 0) {
      lines.push("# Available models");
      for (const name of modelNames) {
        const varBase = toEnvVarName(name);
        lines.push(`${varBase}=${safe(name)}`);
        lines.push(`${varBase}_URL=${safe(proxyEndpoint)}`);
        lines.push("");
      }
    }

    if (event?.mcpServerEndpoints && event.mcpServerEndpoints.length > 0) {
      lines.push("# MCP server endpoints");
      for (const ep of event.mcpServerEndpoints) {
        lines.push(`${toEnvVarName(ep.deploymentName)}_MCP_URL=${safe(ep.endpointUrl)}`);
      }
      lines.push("");
    }

    return lines.join("\n");
  };

  const downloadLabConfig = () => {
    const content = buildLabConfig();
    const safeId = (routeEventId ?? event?.id ?? event?.eventCode ?? "event")
      .trim()
      .toLowerCase()
      .replace(/[^a-z0-9-]+/g, "-")
      .replace(/^-+|-+$/g, "");
    const filename = `event-${safeId || "event"}.env`;
    const blob = new Blob([content], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  };

  const adjustedLocalTime = (
    timestamp: Date,
    utcOffsetInMinutes: number
  ): string => {
    // The backend stores the admin-entered wallclock as DateTimeKind.Utc
    // without converting. Subtract the event's UTC offset to recover the
    // true UTC instant, then let Intl format it in the viewer's local TZ.
    const date = new Date(timestamp);
    date.setMinutes(date.getMinutes() - utcOffsetInMinutes);

    // Get the browser locale
    const locale = navigator.language || navigator.languages[0];

    // Specify the formatting options
    const options: Intl.DateTimeFormatOptions = {
      weekday: "long",
      year: "numeric",
      month: "long",
      day: "numeric",
      hour: "numeric",
      minute: "numeric",
    };

    // Create an Intl.DateTimeFormat object
    const formatter = new Intl.DateTimeFormat(locale, options);
    // Format the date
    const formattedDate = formatter.format(date);
    return formattedDate;
  };

  const trimmedEventCode = event?.eventCode?.trim();

  const nonFoundryModelNames: string[] = (() => {
    if (!event?.capabilities) return [];
    const foundryToolkitNames = new Set(
      (event.foundryToolkitEndpoints ?? []).map((ep: FoundryToolkitEndpoint) => ep.deploymentName)
    );
    return Object.entries(event.capabilities)
      .sort(([a], [b]) => a.localeCompare(b))
      .flatMap(([, names]) =>
        [...names].filter((n) => !foundryToolkitNames.has(n)).sort((a, b) => a.localeCompare(b))
      );
  })();
  const hasNonFoundryModels = nonFoundryModelNames.length > 0;

  const eventStatus: "not-started" | "active" | "ended" | "unknown" = (() => {
    if (!event?.startTimestamp || !event?.endTimestamp) return "unknown";
    const offsetMs = (event.timeZoneOffset ?? 0) * 60 * 1000;
    // Stored timestamps are the event-local wallclock encoded as UTC, so
    // subtract the event's UTC offset to get the true UTC instant.
    const startMs = new Date(event.startTimestamp).getTime() - offsetMs;
    const endMs = new Date(event.endTimestamp).getTime() - offsetMs;
    if (Number.isNaN(startMs) || Number.isNaN(endMs)) return "unknown";
    const now = Date.now();
    if (now < startMs) return "not-started";
    if (now > endMs) return "ended";
    return "active";
  })();
  const isEventActive = eventStatus === "active" || eventStatus === "unknown";

  return (
    <section className={styles.container} >
      <h1>{trimmedEventCode}</h1>
      {event?.startTimestamp && event?.endTimestamp && event?.timeZoneLabel && (
        <div>
          <table>
            <tbody>
              <tr>
                <td>
                  <strong>Starts:</strong>
                </td>
                <td>
                  {adjustedLocalTime(
                    event?.startTimestamp,
                    event?.timeZoneOffset
                  )}
                </td>
              </tr>
              <tr>
                <td>
                  <strong>Ends:</strong>
                </td>
                <td>
                  {adjustedLocalTime(
                    event?.endTimestamp,
                    event?.timeZoneOffset
                  )}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      )}
      {!isEventActive && (
        <h3>
          {eventStatus === "ended"
            ? "This event has ended."
            : "This event has not started yet."}
        </h3>
      )}
      {isEventActive && !(state.profileLoaded && state.profile) && (
        <>
          <h3>Generate your API Key</h3>
          Follow these steps to register and generate your API Key for this event:
          <ol>
            <li>Click <strong>Login with GitHub</strong> in the top right corner.</li>
            <li>Read the event description including the <strong>Terms of use</strong>.</li>
            <li>Scroll to the bottom of the page and click <strong>Register</strong>.</li>
            <li>Next, scroll down to the <strong>Registration Details</strong> section for your API Key and Endpoint.</li>
            <li>Then explore the <strong>Foundry Toolkit</strong>, <strong>MCP Server</strong>, and <strong>SDK</strong> support.</li>
            <li>Forgotten your API Key? Just <strong>revisit</strong> this page.</li>
          </ol>
        </>
      )}
      <div style={{ textAlign: "left", padding: "0px" }}>
        <ReactMarkdown>{event?.eventMarkdown}</ReactMarkdown>
      </div>
      {isEventActive && !attendee && (
        <>
          <h2>Terms of use</h2>
          <div>
            By registering for this event and gaining limited access to Azure AI services for the sole purpose of participating in the "{trimmedEventCode}" event, users acknowledge and agree to use the provided service responsibly and in accordance with the outlined terms. This privilege of limited access to Azure AI services is extended with the expectation that participants will refrain from any form of abuse, including but not limited to, malicious activities, unauthorized access, or any other actions that may disrupt the functionality of the services or compromise the experience for others. We reserve the right to revoke access to the free service in the event of any misuse or violation of these terms. Users are encouraged to engage with the service in a manner that fosters a positive and collaborative community environment.
          </div>
        </>
      )}
      {isEventActive && state.profileLoaded && state.profile && !attendee && (
        <div>
          <Form method="post">
            <Button type="submit" style={{ fontSize: "medium", marginBottom: "40px" }} appearance="primary">
              Register
            </Button>
          </Form>
        </div>
      )}
      {isEventActive && state.profileLoaded && state.profile && attendee && (
        <>
          {((event?.foundryToolkitEndpoints && event.foundryToolkitEndpoints.length > 0) ||
            (event?.mcpServerEndpoints && event.mcpServerEndpoints.length > 0) ||
            hasNonFoundryModels) && (
            <h2>Registration Details</h2>
          )}
          <div className={styles.detailsSection}>
          {event?.foundryToolkitEndpoints && event.foundryToolkitEndpoints.length > 0 && (
            <div className={styles.toolkitCard}>
              <span className={styles.toolkitLabel}>Event API Key:</span>
              <span className={styles.toolkitValue}>
                {state.showApiKey ? attendee.apiKey : "••••••••••••••••••••••••••••••••"}
              </span>
              <div className={styles.fieldRow}>
                <Button
                  icon={state.showApiKey ? <EyeRegular /> : <EyeOffRegular />}
                  onClick={() =>
                    dispatch({ type: "TOGGLE_API_KEY_VISIBILITY" })
                  }
                  size="small"
                />
                <Button
                  icon={<CopyRegular />}
                  onClick={() => copyToClipboard(attendee.apiKey)}
                  size="small"
                />
              </div>
            </div>
          )}
          {event?.foundryToolkitEndpoints && event.foundryToolkitEndpoints.length > 0 && (
            <>
              <h3>Foundry Toolkit Access</h3>
              <p className={styles.toolkitDescription}>
                Use these endpoints to add custom models in the{" "}
                <Link href="https://github.com/microsoft/vscode-ai-toolkit" target="_blank" rel="noopener noreferrer" inline>
                  Foundry Toolkit for VS Code
                </Link>.
                Set your Event API Key as the authentication key when adding the model.
              </p>
              {event.foundryToolkitEndpoints.map((ep: FoundryToolkitEndpoint) => (
                <div key={ep.deploymentName} className={styles.toolkitCard}>
                  <span className={styles.toolkitLabel}>Model:</span>
                  <span className={styles.toolkitValue}>{ep.deploymentName}</span>
                  <Button
                    icon={<CopyRegular />}
                    onClick={() => copyToClipboard(ep.deploymentName)}
                    size="small"
                  />
                  <span className={styles.toolkitLabel}>Endpoint:</span>
                  <span className={styles.toolkitEndpointValue} title={ep.endpointUrl}>{ep.endpointUrl}</span>
                  <Button
                    icon={<CopyRegular />}
                    onClick={() => copyToClipboard(ep.endpointUrl)}
                    size="small"
                  />
                </div>
              ))}
            </>
          )}
          {event?.mcpServerEndpoints && event.mcpServerEndpoints.length > 0 && (
            <>
              <h3>MCP Server Access</h3>
              <p className={styles.toolkitDescription}>
                Use these MCP server URLs directly in your MCP clients. Use your Event API Key as the authentication key.
              </p>
              <div className={styles.toolkitCard}>
                <span className={styles.toolkitLabel}>Event API Key:</span>
                <span className={styles.toolkitValue}>
                  {state.showApiKey ? attendee.apiKey : "••••••••••••••••••••••••••••••••"}
                </span>
                <div className={styles.fieldRow}>
                  <Button
                    icon={state.showApiKey ? <EyeRegular /> : <EyeOffRegular />}
                    onClick={() =>
                      dispatch({ type: "TOGGLE_API_KEY_VISIBILITY" })
                    }
                    size="small"
                  />
                  <Button
                    icon={<CopyRegular />}
                    onClick={() => copyToClipboard(attendee.apiKey)}
                    size="small"
                  />
                </div>
              </div>
              {event.mcpServerEndpoints.map((ep: McpServerEndpoint) => (
                <div key={ep.deploymentName} className={styles.toolkitCard}>
                  <span className={styles.toolkitLabel}>Server:</span>
                  <span className={styles.toolkitValue}>{ep.deploymentName}</span>
                  <Button
                    icon={<CopyRegular />}
                    onClick={() => copyToClipboard(ep.deploymentName)}
                    size="small"
                  />
                  <span className={styles.toolkitLabel}>URL:</span>
                  <span className={styles.toolkitEndpointValue} title={ep.endpointUrl}>{ep.endpointUrl}</span>
                  <Button
                    icon={<CopyRegular />}
                    onClick={() => copyToClipboard(ep.endpointUrl)}
                    size="small"
                  />
                </div>
              ))}
              <p className={styles.toolkitDescription}>
                Python example using the MCP client library (attach your Event API Key in headers):
              </p>
              <div className={styles.codeCardWrapper}>
                <div className={styles.codeCardCopyButton}>
                  <Button
                    icon={<CopyRegular />}
                    onClick={() =>
                      copyToClipboard(
                        "# pip install mcp\n\nimport asyncio\nfrom mcp import ClientSession\nfrom mcp.client.streamable_http import streamablehttp_client\n\nMCP_URL = \"<PASTE_MCP_SERVER_URL>\"\nAPI_KEY = \"<YOUR_EVENT_API_KEY>\"\n\nasync def main():\n    async with streamablehttp_client(\n        MCP_URL,\n        headers={\n            \"api-key\": API_KEY,\n            \"Accept\": \"application/json, text/event-stream\",\n        },\n    ) as (read, write, _):\n        async with ClientSession(read, write) as session:\n            await session.initialize()\n            tools = await session.list_tools()\n            print([tool.name for tool in tools.tools])\n\nif __name__ == \"__main__\":\n    asyncio.run(main())"
                      )
                    }
                    size="small"
                  />
                </div>
                <div className={styles.codeCard}>
                  <pre style={{ margin: 0 }}>
                    <code style={{ lineHeight: "1", fontSize: "medium" }}>
                      {`# pip install mcp

import asyncio
from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

MCP_URL = "<PASTE_MCP_SERVER_URL>"
API_KEY = "<YOUR_EVENT_API_KEY>"

async def main():
    async with streamablehttp_client(
        MCP_URL,
        headers={
            "api-key": API_KEY,
            "Accept": "application/json, text/event-stream",
        },
    ) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = await session.list_tools()
            print([tool.name for tool in tools.tools])

if __name__ == "__main__":
    asyncio.run(main())`}
                    </code>
                  </pre>
                </div>
              </div>
            </>
          )}
          </div>
          {hasNonFoundryModels && (
          <>
          <h3>SDK Access</h3>
          The real power of the Azure OpenAI Service is in the SDKs that allow you to integrate AI capabilities into your applications. You'll need your API Key and the proxy Endpoint to access AI resources using an SDK such as the OpenAI SDK or making REST calls. Or click to download your event keys and endpoints.
          <br />
          <br />
          <Button
            appearance="primary"
            onClick={downloadLabConfig}
            style={{ alignSelf: "flex-start", marginBottom: "12px" }}
          >
            Download event config (.env)
          </Button>
          <p className={styles.toolkitDescription}>
            Saves a <code>event-{(routeEventId ?? event?.id ?? event?.eventCode ?? "event").toString().toLowerCase().replace(/[^a-z0-9-]+/g, "-")}.env</code> file
            containing your <code>EVENT_API_KEY</code>, a <code>MODEL_NAME</code> and matching
            {" "}<code>MODEL_NAME_URL</code> entry for each available model, and any MCP server URLs.
          </p>
          <div className={styles.detailsSection}>
          <div className={styles.toolkitCard}>
            <span className={styles.toolkitLabel}>Event API Key:</span>
            <span className={styles.toolkitValue}>
              {state.showApiKey ? attendee.apiKey : "••••••••••••••••••••••••••••••••"}
            </span>
            <div className={styles.fieldRow}>
              <Button
                icon={state.showApiKey ? <EyeRegular /> : <EyeOffRegular />}
                onClick={() =>
                  dispatch({ type: "TOGGLE_API_KEY_VISIBILITY" })
                }
                size="small"
              />
              <Button
                icon={<CopyRegular />}
                onClick={() => copyToClipboard(attendee.apiKey)}
                size="small"
              />
            </div>
          </div>
          <div className={styles.toolkitCard}>
            <span className={styles.toolkitLabel}>Proxy Endpoint:</span>
            <span className={styles.toolkitEndpointValue} title={event?.proxyUrl ?? `${window.location.origin}/api/v1`}>
              {event?.proxyUrl ?? `${window.location.origin}/api/v1`}
            </span>
            <Button
              icon={<CopyRegular />}
              onClick={() =>
                copyToClipboard(event?.proxyUrl ?? `${window.location.origin}/api/v1`)
              }
              size="small"
            />
          </div>
          {event?.capabilities && hasNonFoundryModels && (
            <div className={styles.toolkitCard}>
              <span className={styles.toolkitLabel}>Available Models:</span>
              <span className={styles.toolkitEndpointValue}>{nonFoundryModelNames.join(", ")}</span>
            </div>
          )}
          </div>
          <h4>Python example using the OpenAI Python SDK</h4>
          The following Python code demonstrates how to use the OpenAI Python SDK to interact with the Azure OpenAI Service.
          <div className={styles.codeCardWrapper}>
          <div className={styles.codeCardCopyButton}>
            <Button
              icon={<CopyRegular />}
              onClick={() => copyToClipboard(`# pip install openai\n\nfrom openai import AzureOpenAI\n\nENDPOINT = "${event?.proxyUrl ?? `${window.location.origin}/api/v1`}"\nAPI_KEY = "<YOUR_EVENT_API_KEY>"\n\nAPI_VERSION = "2024-10-21"\nMODEL_NAME = "gpt-4.1-mini"\n\nclient = AzureOpenAI(\n    azure_endpoint=ENDPOINT,\n    api_key=API_KEY,\n    api_version=API_VERSION,\n)\n\nMESSAGES = [\n    {"role": "system", "content": "You are a helpful assistant."},\n    {"role": "user", "content": "Who won the world series in 2020?"},\n    {\n        "role": "assistant",\n        "content": "The Los Angeles Dodgers won the World Series in 2020.",\n    },\n    {"role": "user", "content": "Where was it played?"},\n]\n\ncompletion = client.chat.completions.create(\n    model=MODEL_NAME,\n    messages=MESSAGES,\n)\n\nprint(completion.model_dump_json(indent=2))`)}
              size="small"
            />
          </div>
          <div className={styles.codeCard}>
          <pre style={{ margin: 0 }}>
            <code style={{ lineHeight: "1", fontSize: "medium" }}>
              {`# pip install openai

from openai import AzureOpenAI

ENDPOINT = "${event?.proxyUrl ?? `${window.location.origin}/api/v1`}"
API_KEY = "<YOUR_EVENT_API_KEY>"

API_VERSION = "2024-10-21"
MODEL_NAME = "gpt-4.1-mini"

client = AzureOpenAI(
    azure_endpoint=ENDPOINT,
    api_key=API_KEY,
    api_version=API_VERSION,
)

MESSAGES = [
    {"role": "system", "content": "You are a helpful assistant."},
    {"role": "user", "content": "Who won the world series in 2020?"},
    {
        "role": "assistant",
        "content": "The Los Angeles Dodgers won the World Series in 2020.",
    },
    {"role": "user", "content": "Where was it played?"},
]

completion = client.chat.completions.create(
    model=MODEL_NAME,
    messages=MESSAGES,
)

print(completion.model_dump_json(indent=2))`}
            </code>
          </pre>
          </div>
          </div>
          <h3 style={{ "marginBottom": "10px" }}>More examples</h3>
          <ul>
            <li>
              <Link
                href="https://learn.microsoft.com/azure/ai-services/openai/quickstart"
                target="_blank"
                rel="noopener noreferrer"
              >
                Quickstart: Get started generating text using Azure OpenAI Service
              </Link>
            </li>
            <li>
              <Link
                href="https://github.com/microsoft/azure-ai-proxy-lite/tree/main/examples"
                target="_blank"
                rel="noopener noreferrer"
              >
                Azure OpenAI Service Proxy Examples
              </Link>
            </li>
          </ul>
          </>
          )}
          <br />
          {/* </div> */}
        </>
      )}

      {state.profileLoaded && !state.profile && (
        <h3>Login with GitHub to register.</h3>
      )}

      <Toaster toasterId={toasterId} />
    </section>
  );
};
