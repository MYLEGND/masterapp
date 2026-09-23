// Business starter content is separate from LEGEND company facts. These are
// explicit editing prompts, not invented services, credentials or testimonials.
// Both consume homeTemplate, hero, section and cards in the canonical renderer.
export const businessHome = {
  hero: {
    businessName: true,
    kicker: 'WELCOME',
    title: 'YOUR BUSINESS',
    tagline: 'Add your business headline',
    text: 'Describe your business and the customers you serve.',
    actions: [
      { style: 'primary', href: '#services', label: 'Explore services' },
      { style: 'ghost', href: '#contact', label: 'Contact us' }
    ],
    image: '',
    imageCaption: ''
  },
  sections: [
    { id:'about', kicker:'ABOUT', title:'Tell your story.', text:'Add your business history and what matters to your team.' },
    { id:'services', kicker:'SERVICES', title:'What you offer.', text:'Replace these prompts with your actual services.', style:'surface', cards:[
      {icon:false,title:'Add a service',text:'Describe the service and who it helps.'},
      {icon:false,title:'Add a service',text:'Describe another service your business provides.'}
    ] },
    { id:'approach', kicker:'OUR APPROACH', title:'How you work.', text:'Explain what customers can expect when working with your business.' },
    { id:'locations', kicker:'WHERE WE WORK', title:'Your locations and service area.', text:'Add your actual locations, opening hours, or service areas.', style:'dark' }
  ],
  contact:{kicker:'CONTACT',title:'Get in touch.',text:'Add your preferred contact information.'}
};

export const businessPages = [
  {key:'home',label:'Home'},
  {key:'about',label:'About',model:{
    business:true,heroKicker:'ABOUT US',heroTitle:'Your business story.',intro:'Introduce your business and the people behind it.',
    milestones:['YOUR BEGINNING','YOUR GROWTH','YOUR BUSINESS'],
    story:[{kicker:'OUR STORY',title:'How you started.',text:'Describe the origins of your business.'},{kicker:'OUR PURPOSE',title:'What matters to you.',text:'Describe your business values and purpose.'}],
    teamKicker:'OUR TEAM',teamName:'Introduce your team.',founder:'Add your team members and their actual experience.',teamCaption:'Your team. Your story.',
    ctaKicker:'CONNECT',ctaTitle:'Start a conversation.',ctaLabel:'Contact us',contactPath:'/business-preview/contact/'
  }},
  {key:'services',label:'Services'},
  {key:'contact',label:'Contact'}
];
