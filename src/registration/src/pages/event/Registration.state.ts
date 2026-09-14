import { ClientPrincipal } from "@aaronpowell/react-static-web-apps-auth";

export type FoundryToolkitEndpoint = {
  deploymentName: string;
  endpointUrl: string;
};

export type McpServerEndpoint = {
  deploymentName: string;
  endpointUrl: string;
};

export type EventDetails = {
  id: string;
  eventCode: string;
  eventMarkdown: string;
  startTimestamp: Date;
  endTimestamp: Date;
  timeZoneLabel: string;
  timeZoneOffset: number;
  showDefaultSamples?: boolean;
  organizerName: string;
  organizerEmail: string;
  proxyUrl?: string;
  foundryToolkitEndpoints?: FoundryToolkitEndpoint[];
  mcpServerEndpoints?: McpServerEndpoint[];
  capabilities?: Record<string, string[]>;
};

export type AttendeeRegistration = { apiKey: string; active: boolean };

export type RegistrationState = {
  profileLoaded: boolean;
  profile?: ClientPrincipal;
  showApiKey: boolean;
};
