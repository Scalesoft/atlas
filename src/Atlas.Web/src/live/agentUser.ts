/** An agent writing for a person (3.6-H): the server shows it as the user „ai:<person>“ (LivePageWrites); people are never „ai:“. */
export const isAgentUser = (userId: string | undefined) => userId?.startsWith("ai:") === true;
