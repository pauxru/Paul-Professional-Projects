export type Track = "foundation" | "dotnet-azure-modernization" | "ai-application-engineering";

export interface DocumentLink {
  title: string;
  url: string;
  kind: string;
}

export interface Project {
  number: number;
  slug: string;
  title: string;
  summary: string;
  track: Track;
  languages: string[];
  focus: string[];
  scope: string;
  sourcePath: string;
  sourceUrl: string;
  readmeUrl: string;
  docsUrl: string;
  documentationCount: number;
  decisionRecordCount: number;
  documents: DocumentLink[];
  readmeHtml: string;
  headings: { id: string; text: string; level: number }[];
  readingMinutes: number;
  featured: boolean;
}

export interface CaseStudy {
  number: number;
  leading: string;
  accent: string;
  summary: string;
  setting: string;
  focus: string;
  tools: string;
  image: string;
  mobileImage?: string;
  imageAlt: string;
  imageCaption: string;
  theme: string;
  responsibility: string;
  problem: string[];
  architecture: { title: string; description: string }[];
  architectureNote: string;
  decisions: { title: string; decision: string; alternative: string; tradeoff: string }[];
  ownership: { title: string; description: string; url: string; label: string }[];
  results: { value: string; label: string; description: string }[];
  resultNote: string;
  evidence: { title: string; description: string; url: string; label: string }[];
  limitations: string;
  nextSteps: string;
}

export interface EngineeringNote {
  slug: string;
  title: string;
  summary: string;
  published: string;
  projectNumber: number;
  topic: string;
  sections: { heading: string; paragraphs: string[]; code?: string }[];
}
