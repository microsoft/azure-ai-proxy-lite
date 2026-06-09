""" Test Azure OpenAI Chat Completions API with Streaming """

# See documentation at https://microsoft.github.io/azure-openai-service-proxy/category/developer-endpoints/

import os
import sys

from dotenv import load_dotenv
from openai import AzureOpenAI

load_dotenv()

ENDPOINT_URL = os.environ.get("PROXY_ENDPOINT")
API_KEY = os.environ.get("PROXY_API_KEY")
API_VERSION = "2024-10-21"
MODEL_NAME = "gpt-4o"


client = AzureOpenAI(
    azure_endpoint=ENDPOINT_URL,
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


try:
    response = client.chat.completions.create(
        model=MODEL_NAME,  # e.g. gpt-4o
        messages=MESSAGES,
        stream=True,
    )
except Exception as exp:
    print(f"Error: {exp}")
    sys.exit(1)


print("Streaming response:")
print("-" * 50)

for chunk in response:
    if chunk.choices and len(chunk.choices) > 0:
        content = chunk.choices[0].delta.content
        if content:
            print(content, end="", flush=True)

print()
print("-" * 50)
